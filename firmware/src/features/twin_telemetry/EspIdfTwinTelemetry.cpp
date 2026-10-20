#include "EspIdfTwinTelemetry.h"
#include "../backend_access/DeviceTokenSession.h"
#include "../backend_access/EspIdfTokenExchangeTransport.h"
#include "esp_crt_bundle.h"
#include "esp_http_client.h"
#include "esp_timer.h"
#include <memory>

bool EspIdfTwinTelemetry::Start() {
  if (task_ || !identity_.IsProvisioned()) return false;
  return xTaskCreate(Task, "twin-telemetry", 8192, this, 2, &task_) == pdPASS;
}
void EspIdfTwinTelemetry::Task(void* context) {
  static_cast<EspIdfTwinTelemetry*>(context)->Run();
  vTaskDelete(nullptr);
}
void EspIdfTwinTelemetry::Run() {
  const auto& settings = station_.Settings();
  if (!settings.IsConfigured()) return;
  EspIdfTokenExchangeTransport exchange(settings.BackendHost(), 443, true);
  DeviceTokenSession tokens(identity_, exchange);
  const std::string url = std::string("https://") + settings.BackendHost() +
      "/v1/twin/ingest/" + identity_.DeviceId();
  std::unique_ptr<TwinTelemetrySnapshot> snapshot(new TwinTelemetrySnapshot);
  uint32_t backoffMs = 500;
  for (;;) {
    vTaskDelay(pdMS_TO_TICKS(backoffMs));
    const uint64_t now = static_cast<uint64_t>(esp_timer_get_time() / 1000);
    if (!station_.HasIp() || station_.IsChangingNetwork() || !tokens.EnsureToken(now)) {
      backoffMs = std::min<uint32_t>(30000, backoffMs * 2);
      continue;
    }
    if (!mailbox_.Take(*snapshot)) { backoffMs = 500; continue; }
    const std::string body = ExportTwinTelemetry(*snapshot, identity_.DeviceId(), bootId_,
        KUFR_FIRMWARE_VERSION, KUFR_FIRMWARE_BUILD_ID, mailbox_.Dropped());
    esp_http_client_config_t config{};
    config.url = url.c_str();
    config.crt_bundle_attach = esp_crt_bundle_attach;
    config.disable_auto_redirect = true;
    config.skip_cert_common_name_check = false;
    config.max_authorization_retries = -1;
    config.timeout_ms = 2000;
    config.buffer_size = 512;
    config.buffer_size_tx = 1024;
    auto client = esp_http_client_init(&config);
    if (!client) { mailbox_.Failed(); continue; }
    const std::string authorization = std::string("Bearer ") + tokens.AccessToken(now);
    const bool configured = esp_http_client_set_method(client, HTTP_METHOD_POST) == ESP_OK
        && esp_http_client_set_header(client, "Authorization", authorization.c_str()) == ESP_OK
        && esp_http_client_set_header(client, "Content-Type", "application/json") == ESP_OK
        && esp_http_client_set_post_field(client, body.c_str(), static_cast<int>(body.size())) == ESP_OK;
    const auto result = configured ? esp_http_client_perform(client) : ESP_FAIL;
    const int status = esp_http_client_get_status_code(client);
    esp_http_client_cleanup(client);
    if (status == 401) tokens.HandleUnauthorized(static_cast<uint64_t>(esp_timer_get_time() / 1000));
    if (result == ESP_OK && status == 204) { backoffMs = 500; }
    else { mailbox_.Failed(); backoffMs = std::min<uint32_t>(30000, backoffMs * 2); }
  }
}

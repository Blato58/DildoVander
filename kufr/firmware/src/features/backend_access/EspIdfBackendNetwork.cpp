#include "EspIdfBackendNetwork.h"

#include <ctime>

#include "esp_log.h"
#include "esp_netif_sntp.h"
#include "esp_random.h"
#include "esp_timer.h"

#include "BuildDate.h"
#include "DeviceTokenSession.h"
#include "EspIdfNetworkSettings.h"
#include "EspIdfTokenExchangeTransport.h"
#include "EspIdfTrustedTimeStore.h"
#include "EspIdfWifiStation.h"
#include "ReconnectPolicy.h"
#include "TrustedTime.h"

namespace {

constexpr char LogTag[] = "kufr_network";
constexpr uint32_t ConnectionTimeoutMs = 15000;
constexpr uint32_t TokenCheckPeriodMs = 60000;
constexpr uint32_t OfflineRetryMs = 30000;

void WaitMs(uint32_t durationMs) {
  vTaskDelay(pdMS_TO_TICKS(durationMs));
}

}  // namespace

EspIdfBackendNetwork::EspIdfBackendNetwork(DeviceIdentity& identity)
    : identity_(identity) {}

bool EspIdfBackendNetwork::Start(bool nvsInitialized) {
  if (task_ != nullptr || !identity_.IsProvisioned() ||
      LoadEspIdfNetworkSettings(settings_, nvsInitialized) !=
          NetworkSettingsLoadResult::Loaded) {
    return false;
  }

  if (xTaskCreate(TaskEntry, "backend_network", 12288, this, 4, &task_) !=
      pdPASS) {
    settings_.Reset();
    task_ = nullptr;
    return false;
  }
  return true;
}

void EspIdfBackendNetwork::TaskEntry(void* argument) {
  EspIdfBackendNetwork* network =
      static_cast<EspIdfBackendNetwork*>(argument);
  network->Run();
  network->task_ = nullptr;
  vTaskDelete(nullptr);
}

void EspIdfBackendNetwork::Run() {
  EspIdfWifiStation wifi;
  if (!wifi.Start(settings_)) {
    ESP_LOGE(LogTag, "Wi-Fi initialization failed; backend offline");
    return;
  }

  EspIdfTrustedTimeStore timeStore(true);
  const uint64_t buildDate = ParseBuildDateUtc(__DATE__);
  TrustedTime trustedTime(timeStore, buildDate);
  if (buildDate == 0 || !trustedTime.IsHealthy()) {
    ESP_LOGE(LogTag, "Time trust unavailable; backend offline");
    return;
  }

  EspIdfTokenExchangeTransport transport(settings_.BackendHost());
  DeviceTokenSession tokens(identity_, transport);
  ReconnectPolicy reconnect;
  bool sntpStarted = false;
  bool timeSynchronized = false;
  bool revokedLogged = false;
  while (true) {
    if (!wifi.HasIp()) {
      if (!wifi.ConnectAndWait(ConnectionTimeoutMs)) {
        WaitMs(reconnect.NextDelayMs(esp_random() % 401));
        continue;
      }
      reconnect.Reset();
      ESP_LOGI(LogTag, "Wi-Fi connected; waiting for trusted time");
    }

    if (!sntpStarted) {
      esp_sntp_config_t config =
          ESP_NETIF_SNTP_DEFAULT_CONFIG("pool.ntp.org");
      if (esp_netif_sntp_init(&config) != ESP_OK) {
        WaitMs(OfflineRetryMs);
        continue;
      }
      sntpStarted = true;
    }
    if (!timeSynchronized) {
      if (esp_netif_sntp_sync_wait(pdMS_TO_TICKS(10000)) != ESP_OK ||
          !wifi.HasIp()) {
        WaitMs(OfflineRetryMs);
        continue;
      }
      timeSynchronized = true;
    }

    const std::time_t currentTime = std::time(nullptr);
    if (currentTime <= 0 ||
        !trustedTime.CheckAndRecord(static_cast<uint64_t>(currentTime))) {
      ESP_LOGW(LogTag, "Time not trusted; backend offline");
      WaitMs(OfflineRetryMs);
      continue;
    }

    const uint64_t nowMs =
        static_cast<uint64_t>(esp_timer_get_time() / 1000);
    if (!tokens.EnsureToken(nowMs)) {
      if (tokens.IsRevoked() && !revokedLogged) {
        ESP_LOGE(LogTag, "Device credential rejected; backend offline");
        revokedLogged = true;
      }
      WaitMs(OfflineRetryMs);
      continue;
    }
    WaitMs(TokenCheckPeriodMs);
  }
}

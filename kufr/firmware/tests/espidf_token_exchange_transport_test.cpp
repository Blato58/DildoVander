#include <cassert>
#include <cstring>
#include <map>
#include <string>

#include "esp_http_client.h"
#include "../src/features/backend_access/EspIdfTokenExchangeTransport.h"

// Reuse the existing HTTP adapter test ABI without altering other fixtures.
struct SpeechStubClient {
  esp_http_client_config_t config;
  esp_http_client_method_t method = HTTP_METHOD_GET;
  std::map<std::string, std::string> headers;
  int postLength = 0;
};

namespace {
constexpr esp_err_t MissingHeader = 0x105;  // ESP_ERR_NOT_FOUND in ESP-IDF 6.1.
const std::string Credential(64, 'a');
const std::string ValidResponse =
    "{\"accessToken\":\"YWJjZA==\",\"tokenType\":\"Bearer\","
    "\"expiresInSeconds\":900,\"deviceId\":\"device-a\"}";
std::string responseBody = ValidResponse;
int status = 200;
int performed = 0;
int cleaned = 0;
bool failHeader = false;
bool failRequest = false;
}

esp_err_t esp_crt_bundle_attach(void*) { return ESP_OK; }
esp_http_client_handle_t esp_http_client_init(const esp_http_client_config_t* config) {
  assert(config->crt_bundle_attach != nullptr);
  assert(config->disable_auto_redirect && !config->skip_cert_common_name_check);
  assert(config->timeout_ms == 5000 && config->max_authorization_retries == -1);
  auto* client = new SpeechStubClient;
  client->config = *config;
  return client;
}
esp_err_t esp_http_client_set_header(esp_http_client_handle_t client, const char* key, const char* value) {
  if (failHeader) { return ESP_FAIL; }
  client->headers[key] = value;
  return ESP_OK;
}
esp_err_t esp_http_client_set_method(esp_http_client_handle_t client, esp_http_client_method_t method) {
  client->method = method;
  return ESP_OK;
}
esp_err_t esp_http_client_set_post_field(esp_http_client_handle_t client, const char* data, int length) {
  client->postLength = length;
  // The pinned SDK returns the missing Content-Type lookup error before perform.
  if (data != nullptr && client->headers.count("Content-Type") == 0) { return MissingHeader; }
  return ESP_OK;
}
int esp_http_client_get_status_code(esp_http_client_handle_t) { return status; }
esp_err_t esp_http_client_cleanup(esp_http_client_handle_t client) {
  ++cleaned;
  delete client;
  return ESP_OK;
}
esp_err_t esp_http_client_perform(esp_http_client_handle_t client) {
  ++performed;
  assert(std::string(client->config.url) == "https://backend.test/v1/device-access/token");
  assert(client->method == HTTP_METHOD_POST && client->postLength == 0);
  assert(client->headers.at("Authorization") == "Bearer " + Credential);
  if (failRequest) { return ESP_FAIL; }
  esp_http_client_event_t event{HTTP_EVENT_ON_DATA, client, client->config.user_data};
  event.data = &responseBody[0];
  event.data_len = static_cast<int>(responseBody.size());
  return client->config.event_handler(&event);
}

int main() {
  EspIdfTokenExchangeTransport transport("backend.test");
  const auto response = transport.Exchange(Credential.c_str());
  // A bodyless POST must reach the server even without a Content-Type header.
  if (response.status != TokenExchangeStatus::Success || performed != 1) { return 1; }
  assert(response.accessToken == "YWJjZA==" && response.deviceId == "device-a");
  assert(response.expiresInSeconds == 900 && cleaned == 1);

  status = 401;
  failRequest = true;  // SDK returns ESP_FAIL for 401 with auth retries disabled.
  assert(transport.Exchange(Credential.c_str()).status == TokenExchangeStatus::Unauthorized);
  assert(performed == 2 && cleaned == 2);

  status = 200;
  assert(transport.Exchange(Credential.c_str()).status == TokenExchangeStatus::Failed);
  failRequest = false;
  responseBody.assign(513, 'x');
  assert(transport.Exchange(Credential.c_str()).status == TokenExchangeStatus::Failed);
  responseBody = ValidResponse;
  failHeader = true;
  const int beforeHeaderFailure = performed;
  assert(transport.Exchange(Credential.c_str()).status == TokenExchangeStatus::Failed);
  assert(performed == beforeHeaderFailure && cleaned == 5);

  const int beforeInvalidCredential = cleaned;
  assert(transport.Exchange("invalid").status == TokenExchangeStatus::Failed);
  assert(cleaned == beforeInvalidCredential && performed == beforeHeaderFailure);
  assert(!EspIdfTokenExchangeTransport("bad/host").IsConfigured());
  return 0;
}

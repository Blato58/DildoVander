#include <cassert>
#include <array>
#include <map>
#include <iostream>
#include "esp_http_client.h"
#include "../src/features/costume_photo/EspIdfCostumeUpload.h"

struct SpeechStubClient {
  esp_http_client_config_t config;
  std::map<std::string, std::string> headers;
  esp_http_client_method_t method = HTTP_METHOD_GET;
  std::string body;
};
namespace {
const std::string Id = "c85743d5-1b72-41b8-8500-ae3c102b27d6";
int requests = 0, cleanups = 0, status = 202;
bool current = true, stopDuringRequest = false;
std::string expectedOperationEpoch;
std::string reply = "{\"operationId\":\"" + Id + "\",\"kind\":\"image\",\"status\":\"pending\"}";
struct Storage : ProvisioningStorage {
  ProvisioningReadResult Read(uint8_t*, size_t, size_t&) override { return ProvisioningReadResult::Missing; }
  bool Write(const uint8_t*, size_t) override { return true; }
};
struct Gate : ServiceGate {
  bool ServiceModeActive() const override { return true; }
  bool ActuatorSupplyDisconnected() const override { return true; }
};
struct Token : TokenExchangePort {
  TokenExchangeResponse Exchange(const char*) override {
    TokenExchangeResponse result; result.status = TokenExchangeStatus::Success;
    result.accessToken = "testToken01"; result.tokenType = "Bearer";
    result.deviceId = "kufr-001"; result.expiresInSeconds = 900; return result;
  }
};
}
esp_err_t esp_crt_bundle_attach(void*) { return ESP_OK; }
int64_t esp_timer_get_time() { return 1000000; }
esp_http_client_handle_t esp_http_client_init(const esp_http_client_config_t* config) {
  assert(config->crt_bundle_attach && config->disable_auto_redirect &&
      !config->skip_cert_common_name_check && config->max_authorization_retries == -1 && config->timeout_ms == 5000);
  auto* client = new SpeechStubClient; client->config = *config; return client;
}
esp_err_t esp_http_client_set_header(esp_http_client_handle_t client, const char* name, const char* value) {
  client->headers[name] = value; return ESP_OK;
}
esp_err_t esp_http_client_set_method(esp_http_client_handle_t client, esp_http_client_method_t method) {
  client->method = method; return ESP_OK;
}
esp_err_t esp_http_client_set_post_field(esp_http_client_handle_t client, const char* data, int length) {
  client->body.assign(data, length); return ESP_OK;
}
esp_err_t esp_http_client_perform(esp_http_client_handle_t client) {
  ++requests;
  assert(client->headers["Authorization"] == "Bearer testToken01");
  if (client->method == HTTP_METHOD_POST) {
    assert(std::string(client->config.url) == "https://backend.test/v1/conversations/" + Id + "/image");
    assert(client->headers["Content-Type"] == "image/jpeg" && client->headers["Idempotency-Key"] == Id);
    assert(client->headers["Boot-Id"] == Id && client->headers["Client-Turn-Id"] == Id);
    assert(client->headers["Generation"] == "2" && client->headers["Device-Revision"] == "3" && client->headers["Catalog-Version"] == "catalog-1");
    if (expectedOperationEpoch.empty()) assert(client->headers.count("Operation-Epoch") == 0);
    else assert(client->headers["Operation-Epoch"] == expectedOperationEpoch);
    assert(!client->body.empty());
  } else assert(client->body.empty());
  if (stopDuringRequest) current = false;
  esp_http_client_event_t event{HTTP_EVENT_ON_DATA, client, client->config.user_data};
  event.data = &reply[0]; event.data_len = static_cast<int>(reply.size());
  return client->config.event_handler(&event);
}
int esp_http_client_get_status_code(esp_http_client_handle_t) { return status; }
esp_err_t esp_http_client_cleanup(esp_http_client_handle_t client) { ++cleanups; delete client; return ESP_OK; }

int main() {
  Storage storage; Gate gate; Token token;
  DeviceIdentity identity(storage);
  std::array<uint8_t, DeviceIdentity::CredentialSize> credential{}; credential.fill(1);
  assert(identity.Provision("kufr-001", credential, gate));
  DeviceTokenSession tokens(identity, token); assert(tokens.EnsureToken(0));
  TurnCorrelation correlation; correlation.deviceId = "kufr-001";
  correlation.bootId = correlation.conversationId = correlation.clientTurnId = Id;
  correlation.generation = 2; correlation.deviceRevision = 3;
  EspIdfCostumeUpload upload("backend.test", tokens, correlation, "catalog-1", Id, []() { return current; });
  const std::vector<uint8_t> jpeg{255,216,255,219,0,3,1,255,196,0,3,1,
      255,192,0,11,8,0,2,0,2,1,1,17,0,255,218,0,8,1,1,0,0,63,0,1,255,217};
  std::string operation;
  assert(upload.Upload(jpeg, operation) && operation == Id && requests == 1 && cleanups == 1);
  status = 401;
  assert(!upload.Upload(jpeg, operation) && requests == 2 && cleanups == 2); // No retry or token refresh replay.
  status = 202; stopDuringRequest = true;
  assert(!upload.Upload(jpeg, operation) && requests == 3 && cleanups == 3);
  assert(!upload.Upload(jpeg, operation) && requests == 3);
  current = true; stopDuringRequest = false; reply.assign(4097, 'x');
  assert(!upload.Upload(jpeg, operation) && cleanups == 4);
  EspIdfCostumeUpload bad("backend.test", tokens, correlation, "catalog\r\nInjected", Id, []() { return true; });
  assert(!bad.Upload(jpeg, operation) && requests == 4);
  correlation.operationEpoch = UINT64_MAX;
  EspIdfCostumeUpload scoped("backend.test", tokens, correlation, "catalog-1", Id, []() { return current; });
  correlation.operationEpoch = 1; // The adapter retains its original envelope.
  expectedOperationEpoch = "18446744073709551615";
  reply = "{\"operationId\":\"" + Id + "\",\"kind\":\"image\",\"status\":\"pending\"}";
  assert(scoped.Upload(jpeg, operation) && operation == Id);
  assert(scoped.Upload(jpeg, operation) && operation == Id);
  std::cout << "Costume JPEG HTTPS headers, TLS, bounds, cleanup and no replay passed\n";
}

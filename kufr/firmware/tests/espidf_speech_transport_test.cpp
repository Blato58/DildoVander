#include <algorithm>
#include <cassert>
#include <cstring>
#include <map>
#include <string>
#include <vector>

#include "esp_http_client.h"
#include "../src/features/conversation/EspIdfSpeechTransport.h"

struct SpeechStubClient {
  esp_http_client_config_t config;
  esp_http_client_method_t method = HTTP_METHOD_GET;
  std::map<std::string, std::string> headers;
  std::string body;
};

namespace {
constexpr char Id[] = "8475532e-92b2-487e-b476-7f98f0030e68";
uint64_t nowMs = 1000;
int status = 200;
int64_t length = 46;
std::vector<uint8_t> bytes;
std::vector<std::pair<std::string, std::string>> headers;
ConversationRuntimeState* mutateRuntime = nullptr;
bool delayBody = false;
std::string sentUrl;
std::string sentBody;
esp_http_client_method_t sentMethod = HTTP_METHOD_GET;
unsigned calls = 0;
}

esp_err_t esp_crt_bundle_attach(void*) { return ESP_OK; }
int64_t esp_timer_get_time() { return static_cast<int64_t>(nowMs * 1000); }
esp_http_client_handle_t esp_http_client_init(const esp_http_client_config_t* config) {
  assert(config->crt_bundle_attach != nullptr && config->disable_auto_redirect &&
         !config->skip_cert_common_name_check && config->max_authorization_retries == -1);
  auto* client = new SpeechStubClient;
  client->config = *config;
  return client;
}
esp_err_t esp_http_client_set_header(esp_http_client_handle_t client, const char* key, const char* value) {
  client->headers[key] = value;
  return ESP_OK;
}
esp_err_t esp_http_client_set_method(esp_http_client_handle_t client, esp_http_client_method_t method) {
  client->method = method;
  return ESP_OK;
}
esp_err_t esp_http_client_set_post_field(esp_http_client_handle_t client, const char* data, int count) {
  client->body.assign(data, count);
  return ESP_OK;
}
int esp_http_client_get_status_code(esp_http_client_handle_t) { return status; }
int64_t esp_http_client_get_content_length(esp_http_client_handle_t) { return length; }
esp_err_t esp_http_client_cleanup(esp_http_client_handle_t client) { delete client; return ESP_OK; }
esp_err_t esp_http_client_perform(esp_http_client_handle_t client) {
  ++calls;
  assert(client->headers["Authorization"] == "Bearer testToken01");
  sentUrl = client->config.url;
  sentBody = client->body;
  sentMethod = client->method;
  if (sentMethod != HTTP_METHOD_GET) { assert(client->headers["Idempotency-Key"] == Id); }
  esp_http_client_event_t event{HTTP_EVENT_ON_HEADER, client, client->config.user_data};
  for (auto& header : headers) {
    event.header_key = &header.first[0];
    event.header_value = &header.second[0];
    if (client->config.event_handler(&event) != ESP_OK) { return ESP_FAIL; }
  }
  if (delayBody) { nowMs += 15000; }
  event.event_id = HTTP_EVENT_ON_DATA;
  event.data = bytes.data();
  event.data_len = static_cast<int>(bytes.size());
  if (mutateRuntime != nullptr) { assert(mutateRuntime->Publish(1, 1, 63, "catalog-1")); }
  return client->config.event_handler(&event);
}

int main() {
  EspIdfSpeechTransport transport("backend.test");
  std::string operationId;
  status = 202;
  const std::string accepted = std::string("{\"operationId\":\"") + Id + "\"}";
  bytes.assign(accepted.begin(), accepted.end());
  assert(transport.Submit(Id, Id, Id, "testToken01", operationId) == 202);
  assert(operationId == Id && sentUrl == std::string("https://backend.test/v1/conversations/") + Id + "/speech");
  assert(sentBody == std::string("{\"turnId\":\"") + Id + "\",\"outputTarget\":\"device\"}");
  assert(transport.Submit(Id, Id, Id, "bad\r\ntoken", operationId) == 0);
  assert(calls == 1);
  bytes.resize(2049);
  assert(transport.Submit(Id, Id, Id, "testToken01", operationId) == 0);
  status = 204;
  assert(transport.Release(Id, Id, "testToken01") == 204);
  assert(sentMethod == HTTP_METHOD_DELETE && sentBody.empty());
  status = 200;
  const std::string polled = std::string("{\"operationId\":\"") + Id +
      "\",\"kind\":\"speech\",\"status\":\"running\",\"correlation\":{\"deviceId\":\"device-a\","
      "\"bootId\":\"" + Id + "\",\"conversationId\":\"" + Id + "\",\"clientTurnId\":\"" + Id +
      "\",\"generation\":0,\"deviceRevision\":0},\"result\":null,\"error\":null}";
  bytes.assign(polled.begin(), polled.end());
  SpeechOperationObservation polledOperation;
  assert(transport.Poll(Id, "testToken01", polledOperation) == 200);
  assert(polledOperation.status == TurnOperationStatus::Running && sentMethod == HTTP_METHOD_GET);

  ConversationRuntimeState runtime(true, 63, "catalog-1");
  ConversationRuntimeSnapshot snapshot;
  assert(runtime.Read(snapshot));
  SpeechOperationObservation operation;
  operation.operationId = Id;
  operation.status = TurnOperationStatus::Completed;
  operation.audioId = Id;
  operation.expiresAt = "2026-10-01T12:02:00Z";
  operation.outputTarget = "device";
  operation.correlation = {"device-a", Id, Id, Id, 0, 0};
  const auto expected = operation.correlation;
  uint64_t utc = 0;
  assert(ParseConversationUtcExpiry("2026-10-01T12:00:00Z", utc));
  bytes.assign(46, 0);
  std::memcpy(bytes.data(), "RIFF", 4);
  bytes[4] = 38;
  std::memcpy(bytes.data() + 8, "WAVEfmt ", 8);
  bytes[16] = 16;
  bytes[20] = bytes[22] = 1;
  bytes[25] = 125;
  bytes[29] = 250;
  bytes[32] = bytes[40] = 2;
  bytes[34] = 16;
  std::memcpy(bytes.data() + 36, "data", 4);
  bytes[44] = bytes[45] = 37;
  const std::vector<std::pair<std::string, std::string>> validHeaders = {
      {"content-type", "audio/wav"}, {"X-Audio-Sample-Rate-Hz", "32000"}, {"X-Audio-Channels", "1"},
      {"Unrelated-Security-Policy", std::string(200, 'x')}};
  std::vector<uint8_t> storage(64, 0);
  SpeechAudioBuffer buffer(storage.data(), storage.size());
  const auto download = [&]() {
    return transport.Download(Id, expected, operation, snapshot, runtime, utc, "testToken01", buffer);
  };
  status = 200;
  headers = validHeaders;
  assert(download() == 200 && sentMethod == HTTP_METHOD_GET);
  uint8_t output[2] = {};
  assert(buffer.ReadPcm(0, output, 2, nowMs, runtime) == 2 && output[0] == 37);
  for (int rejectedStatus : {401, 403, 404, 410, 302}) {
    status = rejectedStatus;
    assert(download() == rejectedStatus);
    assert(std::all_of(storage.begin(), storage.end(), [](uint8_t value) { return value == 0; }));
  }
  status = 200;
  for (int64_t invalidLength : {int64_t(-1), int64_t(48), int64_t(1920046)}) {
    length = invalidLength;
    assert(download() == 0);
  }
  length = 46;
  headers = validHeaders;
  headers[0].second = "audio/mpeg";
  assert(download() == 0);
  headers = validHeaders;
  headers[1].second = "24000";
  assert(download() == 0);
  headers = validHeaders;
  headers.erase(headers.begin() + 2);
  assert(download() == 0);
  headers = validHeaders;
  headers.push_back({"Content-Type", "audio/wav"});
  assert(download() == 0);
  headers = validHeaders;
  delayBody = true;
  assert(download() == 0);
  delayBody = false;
  mutateRuntime = &runtime;
  assert(download() == 0);
  assert(std::all_of(storage.begin(), storage.end(), [](uint8_t value) { return value == 0; }));
}

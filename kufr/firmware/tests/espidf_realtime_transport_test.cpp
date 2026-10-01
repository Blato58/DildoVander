#include <cassert>
#include <cstring>
#include <chrono>
#include <iostream>
#include <map>
#include <mutex>
#include <thread>
#include "esp_http_client.h"
#include "../src/features/voice_turn/EspIdfRealtimeTransport.h"
#include "../src/features/conversation/ConversationStartCodec.h"

struct SpeechStubClient {
  esp_http_client_config_t config;
  std::string url, body;
  std::map<std::string, std::string> headers;
  size_t offset = 0;
  bool live = false;
};
namespace {
const std::string Boot = "c85743d5-1b72-41b8-8500-ae3c102b27d6";
const std::string Conversation = "ac665612-864c-4482-bb68-7415921119ae";
const std::string Turn = "8475532e-92b2-487e-b476-7f98f0030e68";
const std::string Operation = "3d6104de-98c6-40b5-b10d-4f5675a63c51";
std::string stream;
std::atomic<int> uploads{0}, played{0}, drains{0};
std::atomic<bool> listening{false};
bool wrongMime = false, rejectAck = false;
class Audio : public VoiceAudioPort {
 public:
  bool Available() const override { return true; }
  void Listen(bool enabled) override { listening.store(enabled); }
  bool TakeInput(std::vector<uint8_t>& pcm) override {
    std::this_thread::sleep_for(std::chrono::milliseconds(1));
    pcm.assign(listening.load() ? 960 : 0, 0); return true;
  }
  bool Play(const std::vector<uint8_t>& pcm) override { assert(!listening.load()); assert(pcm.size() == 6); ++blocks; return true; }
  bool Drain() override { ++drains; return true; }
  void Stop() override { listening.store(false); }
  bool Failed() const override { return false; }
  int blocks = 0;
};
std::string Correlation(const std::string& turn) {
  return "{\"deviceId\":\"kufr-001\",\"bootId\":\"" + Boot + "\",\"conversationId\":\"" + Conversation +
      "\",\"clientTurnId\":\"" + turn + "\",\"generation\":4,\"deviceRevision\":37}";
}
std::string Reply(const std::string& turn, bool terminal) {
  const std::string correlation = Correlation(turn);
  return "{\"type\":\"started\",\"operationId\":\"" + Operation + "\",\"correlation\":" + correlation + "}\n" +
      "{\"type\":\"audio\",\"delta\":\"AAAAAAAA\"}\n" +
      "{\"type\":\"completed\",\"operationId\":\"" + Operation + "\",\"correlation\":" + correlation +
      ",\"result\":{\"reply\":\"Ahoj\",\"decision\":\"" + (terminal ? "no_selection" : "ask") +
      "\",\"reason\":\"\",\"proposal\":null},\"costMicroUsd\":100}\n";
}
std::string Input(const std::string& turn) {
  return "{\"type\":\"listening\",\"inputId\":\"" + turn + "\"}\n" +
      "{\"type\":\"speech_started\",\"inputId\":\"" + turn + "\"}\n" +
      "{\"type\":\"speech_stopped\",\"inputId\":\"" + turn + "\"}\n";
}
void Reset() { uploads = played = drains = 0; wrongMime = rejectAck = false; listening = false; }
}
int64_t esp_timer_get_time() {
  return std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}
esp_err_t esp_crt_bundle_attach(void*) { return ESP_OK; }
esp_http_client_handle_t esp_http_client_init(const esp_http_client_config_t* config) {
  assert(config->disable_auto_redirect && config->max_authorization_retries == -1 && !config->skip_cert_common_name_check);
  assert(config->crt_bundle_attach != nullptr);
  auto* client = new SpeechStubClient;
  client->config = *config; client->url = config->url;
  client->live = client->url.size() >= 13 && client->url.substr(client->url.size() - 13) == "realtime-live";
  return client;
}
esp_err_t esp_http_client_set_header(esp_http_client_handle_t client, const char* key, const char* value) { client->headers[key] = value; return ESP_OK; }
esp_err_t esp_http_client_set_method(esp_http_client_handle_t, esp_http_client_method_t method) { assert(method == HTTP_METHOD_POST); return ESP_OK; }
esp_err_t esp_http_client_open(esp_http_client_handle_t client, int) { assert(client->headers["Authorization"] == "Bearer testtoken"); return ESP_OK; }
int esp_http_client_write(esp_http_client_handle_t client, const char* data, int count) { client->body.append(data, count); return count; }
int64_t esp_http_client_fetch_headers(esp_http_client_handle_t client) {
  if (client->live) {
    esp_http_client_event_t event{HTTP_EVENT_ON_HEADER, client, client->config.user_data};
    event.header_key = const_cast<char*>("Content-Type");
    event.header_value = const_cast<char*>(wrongMime ? "application/json" : "application/x-ndjson");
    client->config.event_handler(&event);
    assert(IsConversationUuid(client->headers["Idempotency-Key"]));
  } else if (client->url.find("/audio") != std::string::npos) {
    assert(client->body.size() > 0 && client->body.size() <= 9600);
    assert(client->headers["Content-Type"] == "application/octet-stream");
    assert(IsConversationUuid(client->headers["Input-Id"]));
    assert(client->headers.count("Input-Sequence") == 1);
    ++uploads;
  } else {
    assert(client->url.find("/played") != std::string::npos);
    assert(drains.load() > played.load()); // ACK follows the hardware drain.
    assert(client->body == "{\"operationId\":\"" + Operation + "\"}");
    ++played;
  }
  return 0;
}
int esp_http_client_get_status_code(esp_http_client_handle_t client) { return client->live ? 200 : rejectAck && client->url.find("/played") != std::string::npos ? 409 : 204; }
esp_err_t esp_http_client_set_timeout_ms(esp_http_client_handle_t, int) { return ESP_OK; }
int esp_http_client_read(esp_http_client_handle_t client, char* data, int capacity) {
  // Feed arbitrary 7-byte fragments, but give the upload task a turn after
  // listening. The fake VAD stops only after receiving microphone audio.
  if (listening.load() && uploads.load() < played.load()) {
    for (int i = 0; i < 1000 && uploads.load() < played.load(); ++i)
      std::this_thread::sleep_for(std::chrono::milliseconds(1));
    assert(uploads.load() >= played.load());
  }
  const size_t count = std::min<size_t>(std::min(capacity, 7), stream.size() - client->offset);
  std::memcpy(data, stream.data() + client->offset, count); client->offset += count;
  return static_cast<int>(count);
}
esp_err_t esp_http_client_cleanup(esp_http_client_handle_t client) { delete client; return ESP_OK; }
int mbedtls_base64_decode(unsigned char* output, size_t capacity, size_t* used, const unsigned char* input, size_t size) {
  if (size != 8 || std::memcmp(input, "AAAAAAAA", 8) || capacity < 6) return -1;
  *used = 6; std::memset(output, 0, 6); return 0;
}

int main() {
  TurnCorrelation opening;
  opening.deviceId = "kufr-001"; opening.bootId = Boot; opening.conversationId = Conversation;
  opening.clientTurnId = Turn; opening.generation = 4; opening.deviceRevision = 37;
  ConversationRuntimeSnapshot snapshot;
  snapshot.generation = 4; snapshot.deviceRevision = 37; snapshot.catalogVersion = "test-1"; snapshot.readyMask = 63;
  const std::string session = "{\"type\":\"session\",\"streamId\":\"" + Boot + "\",\"audioFormat\":\"pcm16-mono-24000\"}\n";
  const std::string next = "fcfaaf2b-7825-4b9e-a229-e7d8e642f705";
  Reset();
  stream = session + Reply(Turn, false) + Input(next) + Reply(next, false) + Input(Boot) + Reply(Boot, true) +
      "{\"type\":\"closed\",\"reason\":\"conversation_terminal\"}\n";
  Audio audio;
  EspIdfRealtimeTransport transport("example.test", "testtoken", audio, [] { return true; }, [](const char*) {});
  int completed = 0;
  assert(transport.Run("{}", Boot, opening, snapshot, [&](const TurnOperationObservation&) { ++completed; return true; }));
  assert(completed == 3 && played == 3 && uploads >= 2 && audio.blocks == 3);
  for (int scenario = 0; scenario < 4; ++scenario) {
    Reset(); Audio failingAudio;
    stream = session + Reply(Turn, true) + "{\"type\":\"closed\",\"reason\":\"conversation_terminal\"}\n";
    if (scenario == 0) wrongMime = true;
    if (scenario == 1) stream = session + "{\"type\":\"failed\",\"code\":\"cost_limit\"}\n";
    if (scenario == 2) rejectAck = true;
    bool current = true;
    EspIdfRealtimeTransport failed("example.test", "testtoken", failingAudio, [&] { return current; }, [&](const char* phase) {
      if (scenario == 3 && std::strcmp(phase, "speaking") == 0) current = false;
    });
    assert(!failed.Run("{}", Boot, opening, snapshot, [](const TurnOperationObservation&) { return true; }));
    if (scenario == 1) assert(failed.Error() == "cost_limit");
    if (scenario == 3) assert(played == 0 && failingAudio.blocks == 0);
    assert(!listening.load());
  }
  std::cout << "ESP realtime stream, two microphone replies, ordered upload, drain/ACK, Stop and failures passed\n";
}

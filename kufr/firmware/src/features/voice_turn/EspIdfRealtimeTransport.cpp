#include "EspIdfRealtimeTransport.h"
#include <array>
#include <ctime>
#include <cstring>
#include <strings.h>
#include "esp_crt_bundle.h"
#include "esp_http_client.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "mbedtls/base64.h"
#include "../backend_access/NetworkSettings.h"
#include "../conversation/ConversationStartCodec.h"

namespace {
uint64_t NowMs() { return static_cast<uint64_t>(esp_timer_get_time() / 1000); }
void Wipe(std::string& value) { std::fill(value.begin(), value.end(), '\0'); value.clear(); }
struct Client {
  esp_http_client_handle_t value = nullptr;
  bool ndjson = false;
  ~Client() { if (value) esp_http_client_cleanup(value); }
};
esp_err_t ResponseHeader(esp_http_client_event_t* event) {
  if (event->event_id == HTTP_EVENT_ON_HEADER && event->header_key && event->header_value &&
      strcasecmp(event->header_key, "Content-Type") == 0) {
    auto* client = static_cast<Client*>(event->user_data);
    client->ndjson = strcmp(event->header_value, "application/x-ndjson") == 0 ||
        strncmp(event->header_value, "application/x-ndjson;", 21) == 0;
  }
  return ESP_OK;
}
bool Open(Client& client, const std::string& url, const std::string& token, int timeout) {
  esp_http_client_config_t config = {};
  config.url = url.c_str();
  config.crt_bundle_attach = esp_crt_bundle_attach;
  config.disable_auto_redirect = true;
  config.skip_cert_common_name_check = false;
  config.max_authorization_retries = -1;
  config.timeout_ms = timeout;
  config.event_handler = ResponseHeader;
  config.user_data = &client;
  client.value = esp_http_client_init(&config);
  if (!client.value) return false;
  std::string authorization = "Bearer " + token;
  const bool valid = esp_http_client_set_method(client.value, HTTP_METHOD_POST) == ESP_OK &&
      esp_http_client_set_header(client.value, "Authorization", authorization.c_str()) == ESP_OK;
  Wipe(authorization);
  return valid;
}
}

EspIdfRealtimeTransport::EspIdfRealtimeTransport(
    const std::string& host, const std::string& token, VoiceAudioPort& audio,
    std::function<bool()> current, std::function<void(const char*)> phase)
    : token_(token), audio_(audio), current_(std::move(current)), phase_(std::move(phase)) {
  if (NetworkSettings::IsValidBackendHost(host) && !token.empty() && token.size() <= 256 &&
      token.find_first_not_of("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=") == std::string::npos)
    root_ = "https://" + host;
}
EspIdfRealtimeTransport::~EspIdfRealtimeTransport() { Wipe(token_); }

bool EspIdfRealtimeTransport::Current() const {
  return !stop_.load() && !failed_.load() && !audio_.Failed() && current_() &&
      NowMs() - startedAtMs_ < 480000 && NowMs() - lastTurnAtMs_ < 80000;
}

int EspIdfRealtimeTransport::Post(const std::string& path, const void* data, size_t size,
                                const char* contentType, const std::string& inputId, unsigned sequence) {
  if (!Current()) return 0;
  Client client;
  if (!Open(client, root_ + path, token_, 3000) ||
      esp_http_client_set_header(client.value, "Content-Type", contentType) != ESP_OK) return 0;
  if (!inputId.empty()) {
    const std::string order = std::to_string(sequence);
    if (esp_http_client_set_header(client.value, "Input-Id", inputId.c_str()) != ESP_OK ||
        esp_http_client_set_header(client.value, "Input-Sequence", order.c_str()) != ESP_OK) return 0;
  }
  if (esp_http_client_open(client.value, static_cast<int>(size)) != ESP_OK ||
      esp_http_client_write(client.value, static_cast<const char*>(data), static_cast<int>(size)) != static_cast<int>(size) ||
      esp_http_client_fetch_headers(client.value) < 0) return 0;
  return esp_http_client_get_status_code(client.value); // No audio/paid replay on ambiguity.
}

void EspIdfRealtimeTransport::UploadTask(void* argument) {
  auto* transport = static_cast<EspIdfRealtimeTransport*>(argument);
  transport->Upload();
  transport->uploadDone_.store(true);
  vTaskDelete(nullptr);
}
void EspIdfRealtimeTransport::Upload() {
  std::vector<uint8_t> pcm;
  while (Current()) {
    if (!uploading_.load()) { vTaskDelay(pdMS_TO_TICKS(10)); continue; }
    const uint32_t inputEpoch = inputEpoch_.load();
    if (!audio_.TakeInput(pcm)) { failed_.store(true); break; }
    if (pcm.empty()) continue;
    std::lock_guard<std::mutex> lock(inputMutex_);
    if (uploading_.load() && inputEpoch_.load() == inputEpoch && Current()) {
      const std::string path = "/v1/conversations/" + conversationId_ + "/realtime-live/" + streamId_ + "/audio";
      inputBytes_ += pcm.size();
      if (inputBytes_ > 1440000 || Post(path, pcm.data(), pcm.size(), "application/octet-stream", inputId_, sequence_++) != 204)
        failed_.store(true);
    }
    std::fill(pcm.begin(), pcm.end(), 0);
  }
  std::fill(pcm.begin(), pcm.end(), 0);
}

bool EspIdfRealtimeTransport::Run(const std::string& body, const std::string& key,
                                 const TurnCorrelation& opening,
                                 const ConversationRuntimeSnapshot& snapshot,
                                 const std::function<bool(const TurnOperationObservation&)>& completed) {
  if (root_.empty() || body.size() > 4096 || !IsConversationUuid(key) || !audio_.Available()) return false;
  expected_ = opening;
  conversationId_ = opening.conversationId;
  startedAtMs_.store(NowMs());
  lastTurnAtMs_.store(NowMs());
  Client client;
  const std::string url = root_ + "/v1/conversations/" + conversationId_ + "/realtime-live";
  if (!Open(client, url, token_, 5000) ||
      esp_http_client_set_header(client.value, "Content-Type", "application/json") != ESP_OK ||
      esp_http_client_set_header(client.value, "Idempotency-Key", key.c_str()) != ESP_OK ||
      esp_http_client_open(client.value, static_cast<int>(body.size())) != ESP_OK ||
      esp_http_client_write(client.value, body.data(), static_cast<int>(body.size())) != static_cast<int>(body.size()) ||
      esp_http_client_fetch_headers(client.value) < 0 || esp_http_client_get_status_code(client.value) != 200) {
    error_ = "ai_unavailable";
    return false;
  }
  if (!client.ndjson) return false;
  esp_http_client_set_timeout_ms(client.value, 1000);
  TaskHandle_t task = nullptr;
  uploadDone_.store(false);
  if (xTaskCreate(UploadTask, "voice_upload", 8192, this, 3, &task) != pdPASS) {
    uploadDone_.store(true); return false;
  }
  std::array<char, 2048> buffer{};
  std::string line;
  line.reserve(65536);
  size_t total = 0;
  bool valid = true;
  while (Current() && !closed_ && valid) {
    const int count = esp_http_client_read(client.value, buffer.data(), buffer.size());
    if (count == -ESP_ERR_HTTP_EAGAIN) continue;
    if (count <= 0) { valid = false; break; }
    total += static_cast<size_t>(count);
    if (total > 20000000) { valid = false; break; }
    for (int i = 0; i < count; ++i) {
      if (buffer[i] == '\n') {
        RealtimeEvent event;
        valid = Current() && ParseRealtimeEvent(line, event) && Event(event, snapshot, completed);
        Wipe(line);
        Wipe(event.delta);
        Wipe(event.operation.reply);
        if (!valid || closed_) break;
      } else if (line.size() >= 65535) { valid = false; break; }
      else line.push_back(buffer[i]);
    }
  }
  stop_.store(true);
  uploading_.store(false);
  audio_.Stop();
  // Upload HTTP has a bounded deadline; keep this object's task arguments alive
  // until the worker leaves. The independent safety owner continues running.
  while (!uploadDone_.load()) vTaskDelay(pdMS_TO_TICKS(10));
  Wipe(line);
  if (!valid || !closed_ || failed_.load() || audio_.Failed()) {
    if (error_.empty()) error_ = "audio_failed";
    return false;
  }
  return true;
}

bool EspIdfRealtimeTransport::Event(const RealtimeEvent& event,
                                   const ConversationRuntimeSnapshot& snapshot,
                                   const std::function<bool(const TurnOperationObservation&)>& completed) {
  if (event.type == "failed") {
    error_ = event.code == "cost_limit" ? "cost_limit" : "ai_unavailable";
    return false;
  }
  if (event.type == "heartbeat") return sessionSeen_;
  if (event.type == "session") {
    if (sessionSeen_) return false;
    sessionSeen_ = true; streamId_ = event.streamId; return true;
  }
  if (!sessionSeen_) return false;
  if (event.type == "listening") {
    if (terminal_ || !operationId_.empty() || uploading_.load()) return false;
    std::lock_guard<std::mutex> lock(inputMutex_);
    expected_.clientTurnId = event.inputId;
    inputId_ = event.inputId; sequence_ = 0; inputBytes_ = 0;
    inputEpoch_.fetch_add(1);
    lastTurnAtMs_ = NowMs();
    audio_.Listen(true); uploading_.store(true); phase_("listening"); return true;
  }
  if (event.type == "speech_started") return uploading_.load() && event.inputId == inputId_;
  if (event.type == "speech_stopped") {
    if (!uploading_.load() || event.inputId != inputId_) return false;
    uploading_.store(false); inputEpoch_.fetch_add(1);
    audio_.Listen(false); phase_("thinking"); return true;
  }
  if (event.type == "started") {
    if (terminal_ || !operationId_.empty() || uploading_.load() || ++turns_ > 10) return false;
    const auto decision = EvaluateTurnOperation(event.operationId, expected_, snapshot.generation,
                                               snapshot.deviceRevision, event.operation);
    if (decision != TurnPollDecision::ReplyReady) return false;
    operationId_ = event.operationId; audioBytes_ = 0; phase_("thinking"); return true;
  }
  if (event.type == "visitor") return !operationId_.empty();
  if (event.type == "audio") {
    if (operationId_.empty()) return false;
    std::vector<uint8_t> pcm(32768);
    size_t size = 0;
    const bool decoded = mbedtls_base64_decode(pcm.data(), pcm.size(), &size,
        reinterpret_cast<const unsigned char*>(event.delta.data()), event.delta.size()) == 0;
    audioBytes_ += size;
    if (!decoded || !size || size % 2 || audioBytes_ > 1440000) {
      std::fill(pcm.begin(), pcm.end(), 0); return false;
    }
    pcm.resize(size); phase_("speaking");
    const bool played = Current() && audio_.Play(pcm);
    std::fill(pcm.begin(), pcm.end(), 0);
    return played;
  }
  if (event.type == "completed") {
    if (operationId_.empty() || event.operationId != operationId_ || audioBytes_ == 0 || !Current()) return false;
    const std::time_t utc = std::time(nullptr);
    if (utc <= 0) return false;
    const auto decision = EvaluateTurnOperationWithProposal(operationId_, expected_, snapshot.generation,
        snapshot.deviceRevision, snapshot.catalogVersion, snapshot.readyMask, static_cast<uint64_t>(utc), event.operation);
    if (decision != TurnPollDecision::ReplyReady && decision != TurnPollDecision::ProposalReady) return false;
    if (!audio_.Drain() || !Current() || !completed(event.operation)) return false;
    const std::string body = "{\"operationId\":\"" + operationId_ + "\"}";
    const std::string path = "/v1/conversations/" + conversationId_ + "/realtime-live/" + streamId_ + "/played";
    if (Post(path, body.data(), body.size(), "application/json") != 204) return false;
    terminal_ = event.operation.decision != "ask" || turns_ == 10;
    operationId_.clear(); phase_(terminal_ ? "completed" : "thinking"); return true;
  }
  if (event.type == "closed") {
    if (!terminal_ || !operationId_.empty()) return false;
    closed_ = true; return true;
  }
  return false;
}

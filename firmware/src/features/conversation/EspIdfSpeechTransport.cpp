#include "EspIdfSpeechTransport.h"

#include <algorithm>
#include <array>
#include <cstring>

#include "esp_crt_bundle.h"
#include "esp_http_client.h"
#include "esp_timer.h"

#include "NetworkSettings.h"
#include "ConversationStartCodec.h"
#include "OperationAcceptedCodec.h"

namespace {
uint64_t NowMs() { return static_cast<uint64_t>(esp_timer_get_time() / 1000); }

void Wipe(char* bytes, size_t length) {
  volatile char* destination = bytes;
  for (size_t index = 0; index < length; ++index) { destination[index] = 0; }
}

bool BuildAuthorization(const char* token, std::array<char, 264>& header) {
  if (token == nullptr) { return false; }
  const size_t length = strnlen(token, 257);
  if (length == 0 || length > 256) { return false; }
  for (size_t index = 0; index < length; ++index) {
    const char value = token[index];
    if (!((value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z') ||
          (value >= '0' && value <= '9') || value == '+' || value == '/' || value == '=')) {
      return false;
    }
  }
  std::memcpy(header.data(), "Bearer ", 7);
  std::memcpy(header.data() + 7, token, length);
  return true;
}

struct JsonResponse {
  std::string body;
  bool overflow = false;
};

esp_err_t ReceiveJson(esp_http_client_event_t* event) {
  if (event->event_id != HTTP_EVENT_ON_DATA || event->data_len == 0) { return ESP_OK; }
  auto* response = static_cast<JsonResponse*>(event->user_data);
  const int status = esp_http_client_get_status_code(event->client);
  if (status != 200 && status != 202) { return ESP_OK; }
  if (event->data_len < 0 || static_cast<size_t>(event->data_len) > 2048 - response->body.size()) {
    response->overflow = true;
    return ESP_FAIL;
  }
  response->body.append(static_cast<const char*>(event->data), event->data_len);
  return ESP_OK;
}

esp_http_client_config_t Configuration(const std::string& url) {
  esp_http_client_config_t config = {};
  config.url = url.c_str();
  config.crt_bundle_attach = esp_crt_bundle_attach;
  config.disable_auto_redirect = true;
  config.skip_cert_common_name_check = false;
  config.max_authorization_retries = -1;
  config.timeout_ms = 5000;
  return config;
}

struct AudioResponse {
  SpeechAudioBuffer& buffer;
  const std::string& operationId;
  const TurnCorrelation& expected;
  const SpeechOperationObservation& operation;
  const ConversationRuntimeSnapshot& snapshot;
  const ConversationRuntimeState& runtime;
  uint64_t utcSeconds;
  uint64_t startedAtMs;
  std::string contentType;
  std::string sampleRate;
  std::string channels;
  bool started = false;
  bool rejected = false;
};

bool HeaderIs(const char* value, const char* expected) {
  if (value == nullptr) { return false; }
  while (*value != '\0' && *expected != '\0') {
    const char actual = *value >= 'A' && *value <= 'Z'
        ? static_cast<char>(*value + ('a' - 'A')) : *value;
    const char wanted = *expected >= 'A' && *expected <= 'Z'
        ? static_cast<char>(*expected + ('a' - 'A')) : *expected;
    if (actual != wanted) { return false; }
    ++value;
    ++expected;
  }
  return *value == '\0' && *expected == '\0';
}

esp_err_t ReceiveAudio(esp_http_client_event_t* event) {
  auto* response = static_cast<AudioResponse*>(event->user_data);
  if (event->event_id == HTTP_EVENT_ON_HEADER) {
    if (!HeaderIs(event->header_key, "Content-Type") &&
        !HeaderIs(event->header_key, "X-Audio-Sample-Rate-Hz") &&
        !HeaderIs(event->header_key, "X-Audio-Channels")) {
      return ESP_OK;
    }
    if (event->header_value == nullptr || strnlen(event->header_value, 65) > 64) {
      response->rejected = true;
      return ESP_FAIL;
    }
    if (HeaderIs(event->header_key, "Content-Type")) {
      if (!response->contentType.empty()) { response->rejected = true; }
      response->contentType = event->header_value;
    } else if (HeaderIs(event->header_key, "X-Audio-Sample-Rate-Hz")) {
      if (!response->sampleRate.empty()) { response->rejected = true; }
      response->sampleRate = event->header_value;
    } else if (HeaderIs(event->header_key, "X-Audio-Channels")) {
      if (!response->channels.empty()) { response->rejected = true; }
      response->channels = event->header_value;
    }
  }
  if (event->event_id != HTTP_EVENT_ON_DATA || event->data_len == 0 ||
      esp_http_client_get_status_code(event->client) != 200) {
    return ESP_OK;
  }
  const uint64_t now = NowMs();
  if (response->rejected || event->data_len < 0 ||
      now < response->startedAtMs || now - response->startedAtMs >= 15000 ||
      !response->runtime.Matches(response->snapshot)) {
    response->rejected = true;
    response->buffer.Clear();
    return ESP_FAIL;
  }
  if (!response->started) {
    const int64_t length = esp_http_client_get_content_length(event->client);
    const uint64_t elapsedSeconds = (now - response->startedAtMs + 999) / 1000;
    response->started = length > 0 && length <= static_cast<int64_t>(SpeechAudioBuffer::MaxWavBytes) &&
        response->sampleRate == "32000" && response->channels == "1" &&
        response->utcSeconds <= UINT64_MAX - elapsedSeconds &&
        response->buffer.Begin(response->operationId, response->expected,
            response->operation, response->snapshot, response->runtime,
            response->utcSeconds + elapsedSeconds, now,
            static_cast<size_t>(length), response->contentType, 32000, 1);
    if (!response->started) {
      response->rejected = true;
      response->buffer.Clear();
      return ESP_FAIL;
    }
  }
  if (!response->buffer.Append(static_cast<const uint8_t*>(event->data),
                              event->data_len, now, response->runtime)) {
    response->rejected = true;
    return ESP_FAIL;
  }
  return ESP_OK;
}
}  // namespace

EspIdfSpeechTransport::EspIdfSpeechTransport(const std::string& host) {
  if (NetworkSettings::IsValidBackendHost(host)) { baseUrl_ = "https://" + host + "/v1/"; }
}

int EspIdfSpeechTransport::JsonRequest(const std::string& path,
    const std::string& body, const std::string& key, const char* token,
    std::string& responseBody, bool release) {
  responseBody.clear();
  std::array<char, 264> authorization{};
  if (baseUrl_.empty() || !BuildAuthorization(token, authorization)) { return 0; }
  const std::string url = baseUrl_ + path;
  auto config = Configuration(url);
  JsonResponse response;
  config.event_handler = ReceiveJson;
  config.user_data = &response;
  auto client = esp_http_client_init(&config);
  int status = 0;
  if (client != nullptr) {
    bool configured = esp_http_client_set_header(client, "Authorization", authorization.data()) == ESP_OK;
    if (release) {
      configured = configured && esp_http_client_set_method(client, HTTP_METHOD_DELETE) == ESP_OK &&
          esp_http_client_set_header(client, "Idempotency-Key", key.c_str()) == ESP_OK;
    } else if (!body.empty()) {
      configured = configured && esp_http_client_set_method(client, HTTP_METHOD_POST) == ESP_OK &&
          esp_http_client_set_header(client, "Content-Type", "application/json") == ESP_OK &&
          esp_http_client_set_header(client, "Idempotency-Key", key.c_str()) == ESP_OK &&
          esp_http_client_set_post_field(client, body.c_str(), static_cast<int>(body.size())) == ESP_OK;
    }
    const auto error = configured ? esp_http_client_perform(client) : ESP_FAIL;
    const int observed = esp_http_client_get_status_code(client);
    if (!response.overflow && (error == ESP_OK || observed == 401)) { status = observed; }
    if (status == 200 || status == 202) { responseBody = response.body; }
    esp_http_client_cleanup(client);
  }
  Wipe(authorization.data(), authorization.size());
  std::fill(response.body.begin(), response.body.end(), '\0');
  return status;
}

int EspIdfSpeechTransport::Submit(const std::string& conversationId,
    const std::string& turnId, const std::string& idempotencyKey,
    const char* token, std::string& operationId) {
  operationId.clear();
  std::string body;
  if (!IsConversationUuid(conversationId) || !IsConversationUuid(idempotencyKey) ||
      !BuildSpeechRequest(turnId, body)) { return 0; }
  std::string response;
  const int status = JsonRequest("conversations/" + conversationId + "/speech",
                                body, idempotencyKey, token, response);
  return status == 202 && !ParseOperationAccepted(response.data(), response.size(), operationId) ? 0 : status;
}

int EspIdfSpeechTransport::Poll(const std::string& operationId,
    const char* token, SpeechOperationObservation& operation) {
  operation = {};
  if (!IsConversationUuid(operationId)) { return 0; }
  std::string response;
  const int status = JsonRequest("operations/" + operationId, "", "", token, response);
  return status == 200 && !ParseSpeechOperation(response.data(), response.size(), operation) ? 0 : status;
}

int EspIdfSpeechTransport::Release(const std::string& operationId,
    const std::string& idempotencyKey, const char* token) {
  if (!IsConversationUuid(operationId) || !IsConversationUuid(idempotencyKey)) { return 0; }
  std::string response;
  return JsonRequest("operations/" + operationId, "", idempotencyKey, token, response, true);
}

int EspIdfSpeechTransport::Download(const std::string& operationId,
    const TurnCorrelation& expected, const SpeechOperationObservation& operation,
    const ConversationRuntimeSnapshot& snapshot,
    const ConversationRuntimeState& runtime, uint64_t utcSeconds,
    const char* token, SpeechAudioBuffer& buffer) {
  buffer.Clear();
  std::array<char, 264> authorization{};
  if (baseUrl_.empty() || SpeechRemainingMs(operationId, expected, utcSeconds, operation) == 0 ||
      !runtime.Matches(snapshot) || !BuildAuthorization(token, authorization)) { return 0; }
  const std::string url = baseUrl_ + "audio/" + operation.audioId;
  auto config = Configuration(url);
  AudioResponse response{buffer, operationId, expected, operation, snapshot, runtime,
                         utcSeconds, NowMs(), "", "", "", false, false};
  config.event_handler = ReceiveAudio;
  config.user_data = &response;
  auto client = esp_http_client_init(&config);
  int status = 0;
  if (client != nullptr) {
    const bool configured = esp_http_client_set_header(client, "Authorization", authorization.data()) == ESP_OK;
    const auto error = configured ? esp_http_client_perform(client) : ESP_FAIL;
    const int observed = esp_http_client_get_status_code(client);
    if (error == ESP_OK || observed == 401) { status = observed; }
    esp_http_client_cleanup(client);
    if (status == 200 && (response.rejected || !response.started ||
        NowMs() - response.startedAtMs >= 15000 || !buffer.Finish(NowMs(), runtime))) { status = 0; }
  }
  Wipe(authorization.data(), authorization.size());
  if (status != 200) { buffer.Clear(); }
  return status;
}

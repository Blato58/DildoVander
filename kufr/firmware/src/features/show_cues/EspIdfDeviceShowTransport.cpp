#include "EspIdfDeviceShowTransport.h"
#include <array>
#include <cstring>
#include <strings.h>
#include "esp_crt_bundle.h"
#include "esp_heap_caps.h"
#include "esp_http_client.h"
#include "esp_memory_utils.h"
#include "esp_timer.h"
#include "../backend_access/NetworkSettings.h"

namespace {
uint64_t NowMs() { return static_cast<uint64_t>(esp_timer_get_time() / 1000); }
void Wipe(char* data, size_t length) { volatile char* bytes = data; while (length--) *bytes++ = 0; }
bool Authorization(const char* token, std::array<char, 264>& output) {
  if (!token || !*token || std::strlen(token) > 256) return false;
  for (const char* item = token; *item; ++item) if (*item <= 32 || *item >= 127) return false;
  std::memcpy(output.data(), "Bearer ", 7); std::memcpy(output.data() + 7, token, std::strlen(token)); return true;
}
esp_http_client_config_t Config(const std::string& url) {
  esp_http_client_config_t config{};
  config.url = url.c_str(); config.crt_bundle_attach = esp_crt_bundle_attach;
  config.disable_auto_redirect = true; config.skip_cert_common_name_check = false;
  config.max_authorization_retries = -1; config.timeout_ms = 1000;
  return config;
}
struct JsonResponse { std::string bytes; bool overflow = false; };
esp_err_t JsonEvent(esp_http_client_event_t* event) {
  auto& response = *static_cast<JsonResponse*>(event->user_data);
  if (event->event_id == HTTP_EVENT_ON_DATA && event->data_len > 0) {
    if (response.bytes.size() + event->data_len > 4096) { response.overflow = true; return ESP_FAIL; }
    response.bytes.append(static_cast<const char*>(event->data), event->data_len);
  }
  return ESP_OK;
}
struct AudioResponse {
  DeviceShowAudio& audio;
  const DeviceShowMailbox& mailbox;
  const std::string& cueId;
  bool rejected = false;
  std::string contentType;
  int64_t expectedBytes = 0;
};
esp_err_t AudioEvent(esp_http_client_event_t* event) {
  auto& response = *static_cast<AudioResponse*>(event->user_data);
  if (!response.mailbox.PlaybackCurrent(response.cueId, NowMs())) { response.rejected = true; return ESP_FAIL; }
  if (event->event_id == HTTP_EVENT_ON_HEADER && event->header_key && event->header_value &&
      strcasecmp(event->header_key, "Content-Type") == 0) response.contentType = event->header_value;
  if (event->event_id != HTTP_EVENT_ON_DATA || esp_http_client_get_status_code(event->client) != 200) return ESP_OK;
  if (event->data_len == 0) return ESP_OK;
  const int64_t length = esp_http_client_get_content_length(event->client);
  if (length < 46 || length > static_cast<int64_t>(DeviceShowAudio::MaximumBytes) ||
      response.contentType != "audio/wav" || event->data_len < 0 ||
      !response.audio.Append(static_cast<const uint8_t*>(event->data), event->data_len)) {
    response.rejected = true; return ESP_FAIL;
  }
  response.expectedBytes = length;
  return ESP_OK;
}
}

EspIdfDeviceShowTransport::EspIdfDeviceShowTransport(const std::string& host) {
  if (NetworkSettings::IsValidBackendHost(host)) root_ = "https://" + host;
}
std::shared_ptr<DeviceShowAudio> EspIdfDeviceShowTransport::AllocateAudio() {
  auto* storage = static_cast<uint8_t*>(heap_caps_malloc(DeviceShowAudio::MaximumBytes, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT));
  if (!storage) return {};
  if (!esp_ptr_external_ram(storage) || heap_caps_get_allocated_size(storage) < DeviceShowAudio::MaximumBytes) { heap_caps_free(storage); return {}; }
  return std::make_shared<DeviceShowAudio>(storage, DeviceShowAudio::MaximumBytes, [](uint8_t* bytes) { heap_caps_free(bytes); });
}
int EspIdfDeviceShowTransport::JsonRequest(const std::string& path, const std::string& body,
    const char* token, std::string& received) {
  received.clear();
  std::array<char, 264> authorization{};
  if (root_.empty() || !Authorization(token, authorization)) return 0;
  const std::string url = root_ + path;
  auto config = Config(url); JsonResponse response;
  config.event_handler = JsonEvent; config.user_data = &response;
  auto client = esp_http_client_init(&config);
  int status = 0;
  if (client) {
    bool ready = esp_http_client_set_header(client, "Authorization", authorization.data()) == ESP_OK;
    if (!body.empty()) ready = ready && esp_http_client_set_method(client, HTTP_METHOD_POST) == ESP_OK &&
        esp_http_client_set_header(client, "Content-Type", "application/json") == ESP_OK &&
        esp_http_client_set_post_field(client, body.c_str(), static_cast<int>(body.size())) == ESP_OK;
    const esp_err_t result = ready ? esp_http_client_perform(client) : ESP_FAIL;
    const int observed = esp_http_client_get_status_code(client);
    if (!response.overflow && (result == ESP_OK || observed == 401)) status = observed;
    esp_http_client_cleanup(client);
  }
  Wipe(authorization.data(), authorization.size()); received = std::move(response.bytes); return status;
}
int EspIdfDeviceShowTransport::Poll(const std::string& bootId, uint64_t generation, uint64_t revision,
    uint64_t deviceMs, const DeviceShowSettings& applied, const std::string& voice, const char* token, DeviceShowEnvelope& envelope) {
  if (voice != "marin" && voice != "cedar" && voice != "coral" && voice != "onyx") return 0;
  const std::string path = "/v1/show/device-cues?bootId=" + bootId + "&generation=" + std::to_string(generation) +
      "&deviceRevision=" + std::to_string(revision) + "&deviceTimeMs=" + std::to_string(deviceMs) +
      "&appliedSettingsRevision=" + std::to_string(applied.revision) + "&appliedQuiet=" + (applied.quiet ? "true" : "false") +
      "&appliedAmbient=" + (applied.ambient ? "true" : "false") +
      "&appliedStopRevision=" + std::to_string(applied.stopRevision) + "&voice=" + voice;
  std::string received;
  const int status = JsonRequest(path, "", token, received);
  return status == 200 && !ParseDeviceShowEnvelope(received, envelope) ? 0 : status;
}
int EspIdfDeviceShowTransport::Ack(const DeviceShowAck& ack, const char* token) {
  std::string received;
  return JsonRequest("/v1/show/device-cues/" + ack.cue.cueId + "/ack", DeviceShowAckJson(ack), token, received);
}
int EspIdfDeviceShowTransport::Download(const DeviceShowCue& cue, const char* token,
    DeviceShowAudio& audio, const DeviceShowMailbox& mailbox) {
  audio.Reset();
  std::array<char, 264> authorization{};
  if (root_.empty() || cue.audioPath != "/v1/show/device-cues/" + cue.cueId + "/audio" ||
      !Authorization(token, authorization) || !mailbox.PlaybackCurrent(cue.cueId, NowMs())) return 0;
  const std::string url = root_ + cue.audioPath;
  auto config = Config(url);
  AudioResponse response{audio, mailbox, cue.cueId, false, "", 0};
  config.event_handler = AudioEvent; config.user_data = &response;
  auto client = esp_http_client_init(&config);
  int status = 0;
  if (client) {
    const bool ready = esp_http_client_set_header(client, "Authorization", authorization.data()) == ESP_OK;
    const esp_err_t result = ready ? esp_http_client_perform(client) : ESP_FAIL;
    const int observed = esp_http_client_get_status_code(client);
    if (result == ESP_OK || observed == 401) status = observed;
    esp_http_client_cleanup(client);
    if (status == 200 && (response.rejected || response.expectedBytes < 46 ||
        !mailbox.PlaybackCurrent(cue.cueId, NowMs()) || !audio.Finish() ||
        audio.PcmBytes() + 44 != static_cast<size_t>(response.expectedBytes))) status = 0;
  }
  Wipe(authorization.data(), authorization.size());
  if (status != 200) audio.Reset();
  return status;
}

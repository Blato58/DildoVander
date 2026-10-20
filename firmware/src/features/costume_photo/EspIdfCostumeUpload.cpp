#include "EspIdfCostumeUpload.h"
#include <array>
#include <cstring>
#include "esp_crt_bundle.h"
#include "esp_http_client.h"
#include "esp_timer.h"
#include "NetworkSettings.h"
#include "../conversation/ConversationStartCodec.h"

namespace {
struct Response { std::string body; bool overflow = false; const std::function<bool()>* current; };
esp_err_t OnHttpEvent(esp_http_client_event_t* event) {
  auto& response = *static_cast<Response*>(event->user_data);
  if (!(*response.current)()) return ESP_FAIL;
  if (event->event_id == HTTP_EVENT_ON_DATA && event->data_len > 0) {
    if (response.body.size() + static_cast<size_t>(event->data_len) > 4096) { response.overflow = true; return ESP_FAIL; }
    response.body.append(static_cast<const char*>(event->data), event->data_len);
  }
  return ESP_OK;
}
uint64_t NowMs() { return static_cast<uint64_t>(esp_timer_get_time() / 1000); }
}

EspIdfCostumeUpload::EspIdfCostumeUpload(const std::string& host, DeviceTokenSession& tokens,
    const TurnCorrelation& correlation, const std::string& catalog,
    const std::string& idempotencyKey, const std::function<bool()>& current)
    : catalog_(catalog), key_(idempotencyKey), tokens_(tokens), correlation_(correlation), current_(current) {
  if (NetworkSettings::IsValidBackendHost(host) && IsConversationUuid(key_) &&
      IsConversationUuid(correlation.conversationId) && IsConversationUuid(correlation.bootId) &&
      IsConversationUuid(correlation.clientTurnId) && !catalog.empty() && catalog.size() <= 128 &&
      catalog.find_first_not_of("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.") == std::string::npos) baseUrl_ = "https://" + host;
}

int EspIdfCostumeUpload::Request(const std::string& path, const std::vector<uint8_t>* jpeg, std::string& response) {
  response.clear();
  const char* token = tokens_.AccessToken(NowMs());
  if (baseUrl_.empty() || !current_() || !token) return 0;
  const size_t tokenLength = strnlen(token, 257);
  if (!tokenLength || tokenLength > 256) return 0;
  for (size_t i = 0; i < tokenLength; ++i) if (!std::strchr("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=", token[i])) return 0;
  Response received{{}, false, &current_};
  const std::string url = baseUrl_ + path;
  esp_http_client_config_t config{};
  config.url = url.c_str();
  config.crt_bundle_attach = esp_crt_bundle_attach;
  config.disable_auto_redirect = true;
  config.skip_cert_common_name_check = false;
  config.max_authorization_retries = -1;
  config.timeout_ms = 5000;
  config.event_handler = OnHttpEvent;
  config.user_data = &received;
  esp_http_client_handle_t client = esp_http_client_init(&config);
  if (!client) return 0;
  std::array<char, 264> authorization{};
  std::memcpy(authorization.data(), "Bearer ", 7);
  std::memcpy(authorization.data() + 7, token, tokenLength);
  bool configured = esp_http_client_set_method(client, jpeg ? HTTP_METHOD_POST : HTTP_METHOD_GET) == ESP_OK &&
      esp_http_client_set_header(client, "Authorization", authorization.data()) == ESP_OK;
  if (jpeg) {
    const std::string generation = std::to_string(correlation_.generation), revision = std::to_string(correlation_.deviceRevision);
    configured = configured && esp_http_client_set_header(client, "Content-Type", "image/jpeg") == ESP_OK &&
        esp_http_client_set_header(client, "Idempotency-Key", key_.c_str()) == ESP_OK &&
        esp_http_client_set_header(client, "Client-Turn-Id", correlation_.clientTurnId.c_str()) == ESP_OK &&
        esp_http_client_set_header(client, "Boot-Id", correlation_.bootId.c_str()) == ESP_OK &&
        esp_http_client_set_header(client, "Generation", generation.c_str()) == ESP_OK &&
        esp_http_client_set_header(client, "Device-Revision", revision.c_str()) == ESP_OK &&
        esp_http_client_set_header(client, "Catalog-Version", catalog_.c_str()) == ESP_OK &&
        esp_http_client_set_post_field(client, reinterpret_cast<const char*>(jpeg->data()), static_cast<int>(jpeg->size())) == ESP_OK;
    if (correlation_.operationEpoch != 0) {
      const std::string operationEpoch = std::to_string(correlation_.operationEpoch);
      configured = configured &&
          esp_http_client_set_header(client, "Operation-Epoch", operationEpoch.c_str()) == ESP_OK;
    }
  }
  const esp_err_t performed = configured && current_() ? esp_http_client_perform(client) : ESP_FAIL;
  const int status = performed == ESP_OK && current_() && !received.overflow ? esp_http_client_get_status_code(client) : 0;
  esp_http_client_cleanup(client);
  volatile char* secret = authorization.data();
  for (size_t i = 0; i < authorization.size(); ++i) secret[i] = 0;
  if (status) response = std::move(received.body);
  std::fill(received.body.begin(), received.body.end(), '\0');
  return status;
}
bool EspIdfCostumeUpload::Upload(const std::vector<uint8_t>& jpeg, std::string& operationId) {
  std::string response;
  const bool result = ValidCostumeJpeg(jpeg.data(), jpeg.size()) &&
      Request("/v1/conversations/" + correlation_.conversationId + "/image", &jpeg, response) == 202 &&
      ParseImageAccepted(response, operationId);
  std::fill(response.begin(), response.end(), '\0');
  return result;
}
PhotoPoll EspIdfCostumeUpload::Poll(const std::string& operationId) {
  if (!IsConversationUuid(operationId)) return PhotoPoll::Failed;
  std::string response;
  const auto result = Request("/v1/operations/" + operationId, nullptr, response) == 200 ?
      ParseImageOperation(response, operationId, correlation_) : PhotoPoll::Failed;
  std::fill(response.begin(), response.end(), '\0');
  return result;
}

#include "EspIdfConversationCleanup.h"
#include <algorithm>
#include <array>
#include <cstring>
#include "esp_crt_bundle.h"
#include "NetworkSettings.h"
#include "ConversationStartCodec.h"

namespace {

void Wipe(char* data, size_t length) {
  volatile char* bytes = data;
  for (size_t index = 0; index < length; ++index) {
    bytes[index] = 0;
  }
}

bool IsSafeAccessToken(const char* token) {
  if (token == nullptr) {
    return false;
  }
  const size_t length = strnlen(token, 257);
  if (length == 0 || length > 256) {
    return false;
  }
  for (size_t index = 0; index < length; ++index) {
    const char value = token[index];
    if (!((value >= 'A' && value <= 'Z') ||
          (value >= 'a' && value <= 'z') ||
          (value >= '0' && value <= '9') || value == '+' || value == '/' ||
          value == '=')) {
      return false;
    }
  }
  return true;
}

}  // namespace

EspIdfConversationCleanup::EspIdfConversationCleanup(
    const std::string& backendHost, uint16_t port) {
  if (!NetworkSettings::IsValidBackendHost(backendHost) || port == 0) {
    return;
  }
  baseUrl_ = "https://" + backendHost;
  if (port != 443) {
    baseUrl_ += ":" + std::to_string(port);
  }
  baseUrl_ += "/v1/conversations/";
}

bool EspIdfConversationCleanup::IsConfigured() const {
  return !baseUrl_.empty();
}

int EspIdfConversationCleanup::Delete(
    const std::string& conversationId,
    const std::string& idempotencyKey,
    const char* accessToken) {
  if (!IsConfigured() || !IsConversationUuid(conversationId) ||
      !IsConversationUuid(idempotencyKey) ||
      !IsSafeAccessToken(accessToken)) {
    return 0;
  }
  const std::string url = baseUrl_ + conversationId;
  esp_http_client_config_t config = {};
  config.url = url.c_str();
  config.crt_bundle_attach = esp_crt_bundle_attach;
  config.disable_auto_redirect = true;
  config.skip_cert_common_name_check = false;
  config.max_authorization_retries = -1;
  config.timeout_ms = 5000;
  esp_http_client_handle_t client = esp_http_client_init(&config);
  if (client == nullptr) {
    return 0;
  }
  std::array<char, 7 + 256 + 1> authorization{};
  std::memcpy(authorization.data(), "Bearer ", 7);
  std::memcpy(authorization.data() + 7, accessToken,
              std::strlen(accessToken));
  const bool configured =
      esp_http_client_set_method(client, HTTP_METHOD_DELETE) == ESP_OK &&
      esp_http_client_set_header(client, "Authorization",
                                 authorization.data()) == ESP_OK &&
      esp_http_client_set_header(client, "Idempotency-Key",
                                 idempotencyKey.c_str()) == ESP_OK;
  const esp_err_t result = configured ? esp_http_client_perform(client) : ESP_FAIL;
  const int status = esp_http_client_get_status_code(client);
  esp_http_client_cleanup(client);
  Wipe(authorization.data(), authorization.size());
  return status == 401 || result == ESP_OK ? status : 0;
}

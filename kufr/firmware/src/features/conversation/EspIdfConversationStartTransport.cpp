#include "EspIdfConversationStartTransport.h"

#include <array>
#include <cstring>

#include "esp_crt_bundle.h"

#include "NetworkSettings.h"

namespace {

constexpr char ConversationPath[] = "/v1/conversations";

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

EspIdfConversationStartTransport::EspIdfConversationStartTransport(
    const std::string& backendHost, uint16_t port) {
  if (!NetworkSettings::IsValidBackendHost(backendHost) || port == 0) {
    return;
  }
  url_ = "https://" + backendHost;
  if (port != 443) {
    url_ += ":" + std::to_string(port);
  }
  url_ += ConversationPath;
}

bool EspIdfConversationStartTransport::IsConfigured() const {
  return !url_.empty();
}

ConversationStartHttpResult EspIdfConversationStartTransport::Create(
    const ConversationStartRequest& request, const char* accessToken) {
  ConversationStartHttpResult response;
  std::string body;
  if (!IsConfigured() || !IsSafeAccessToken(accessToken) ||
      !BuildConversationStartRequest(request, body)) {
    return response;
  }

  ResponseBody received;
  esp_http_client_config_t config = {};
  config.url = url_.c_str();
  config.crt_bundle_attach = esp_crt_bundle_attach;
  config.disable_auto_redirect = true;
  config.skip_cert_common_name_check = false;
  config.max_authorization_retries = -1;
  config.timeout_ms = 5000;
  config.event_handler = OnHttpEvent;
  config.user_data = &received;
  esp_http_client_handle_t client = esp_http_client_init(&config);
  if (client == nullptr) {
    return response;
  }

  std::array<char, 7 + 256 + 1> authorization{};
  std::memcpy(authorization.data(), "Bearer ", 7);
  std::memcpy(authorization.data() + 7, accessToken,
              std::strlen(accessToken));
  const bool configured =
      esp_http_client_set_method(client, HTTP_METHOD_POST) == ESP_OK &&
      esp_http_client_set_header(client, "Authorization",
                                 authorization.data()) == ESP_OK &&
      esp_http_client_set_header(client, "Idempotency-Key",
                                 request.idempotencyKey.c_str()) == ESP_OK &&
      esp_http_client_set_header(client, "Content-Type",
                                 "application/json") == ESP_OK &&
      esp_http_client_set_post_field(client, body.c_str(),
                                     static_cast<int>(body.size())) == ESP_OK;
  const esp_err_t result =
      configured ? esp_http_client_perform(client) : ESP_FAIL;
  // ESP-IDF returns ESP_FAIL for HTTP 401 when auth retries are disabled,
  // although the response status has already been parsed.
  const int status = esp_http_client_get_status_code(client);
  if (status == 401 || result == ESP_OK) {
    response.statusCode = status;
  }
  if (response.statusCode == 201 &&
      (received.overflow ||
       !ParseConversationStartResponse(received.data, received.length,
                                       response.conversation))) {
    response.statusCode = 0;
  }
  esp_http_client_cleanup(client);
  Wipe(authorization.data(), authorization.size());
  Wipe(received.data, sizeof(received.data));
  return response;
}

esp_err_t EspIdfConversationStartTransport::OnHttpEvent(
    esp_http_client_event_t* event) {
  if (event->event_id != HTTP_EVENT_ON_DATA || event->data_len == 0 ||
      esp_http_client_get_status_code(event->client) != 201) {
    return ESP_OK;
  }
  ResponseBody* received = static_cast<ResponseBody*>(event->user_data);
  if (received == nullptr || event->data_len < 0 ||
      static_cast<size_t>(event->data_len) >
          ResponseBody::Capacity - received->length) {
    if (received != nullptr) {
      received->overflow = true;
    }
    return ESP_FAIL;
  }
  std::memcpy(received->data + received->length, event->data,
              static_cast<size_t>(event->data_len));
  received->length += static_cast<size_t>(event->data_len);
  return ESP_OK;
}

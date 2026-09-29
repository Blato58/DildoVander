#include "EspIdfTokenExchangeTransport.h"

#include <algorithm>
#include <array>
#include <cstring>

#include "esp_crt_bundle.h"
#include "NetworkSettings.h"
#include "TokenResponseParser.h"

namespace {

constexpr char TokenPath[] = "/v1/device-access/token";
constexpr size_t CredentialHexLength = 64;

void Wipe(char* data, size_t length) {
  volatile char* bytes = data;
  for (size_t index = 0; index < length; ++index) {
    bytes[index] = 0;
  }
}

}  // namespace

EspIdfTokenExchangeTransport::EspIdfTokenExchangeTransport(
    const std::string& backendHost, uint16_t port) {
  if (!NetworkSettings::IsValidBackendHost(backendHost) || port == 0) {
    return;
  }
  url_ = "https://" + backendHost;
  if (port != 443) {
    url_ += ":" + std::to_string(port);
  }
  url_ += TokenPath;
}

bool EspIdfTokenExchangeTransport::IsConfigured() const {
  return !url_.empty();
}

TokenExchangeResponse EspIdfTokenExchangeTransport::Exchange(
    const char* credentialHex) {
  if (!IsConfigured() || credentialHex == nullptr ||
      std::strlen(credentialHex) != CredentialHexLength) {
    return {};
  }
  for (size_t index = 0; index < CredentialHexLength; ++index) {
    const char value = credentialHex[index];
    if (!((value >= '0' && value <= '9') ||
          (value >= 'a' && value <= 'f'))) {
      return {};
    }
  }

  ResponseBody body;
  esp_http_client_config_t config = {};
  config.url = url_.c_str();
  config.crt_bundle_attach = esp_crt_bundle_attach;
  config.disable_auto_redirect = true;
  config.skip_cert_common_name_check = false;
  config.max_authorization_retries = -1;
  config.timeout_ms = 5000;
  config.event_handler = OnHttpEvent;
  config.user_data = &body;
  esp_http_client_handle_t client = esp_http_client_init(&config);
  if (client == nullptr) {
    return {};
  }

  std::array<char, 7 + CredentialHexLength + 1> authorization{};
  std::memcpy(authorization.data(), "Bearer ", 7);
  std::memcpy(authorization.data() + 7, credentialHex, CredentialHexLength);
  const bool configured =
      esp_http_client_set_method(client, HTTP_METHOD_POST) == ESP_OK &&
      esp_http_client_set_header(client, "Authorization",
                                 authorization.data()) == ESP_OK &&
      esp_http_client_set_post_field(client, "", 0) == ESP_OK;
  const esp_err_t result =
      configured ? esp_http_client_perform(client) : ESP_FAIL;
  const int status =
      result == ESP_OK ? esp_http_client_get_status_code(client) : 0;
  esp_http_client_cleanup(client);
  Wipe(authorization.data(), authorization.size());

  TokenExchangeResponse response;
  if (status == 401) {
    response.status = TokenExchangeStatus::Unauthorized;
  } else if (status == 200 && !body.overflow) {
    response = ParseResponse(body);
  }
  Wipe(body.data, sizeof(body.data));
  return response;
}

esp_err_t EspIdfTokenExchangeTransport::OnHttpEvent(
    esp_http_client_event_t* event) {
  if (event->event_id != HTTP_EVENT_ON_DATA) {
    return ESP_OK;
  }
  if (event->data_len == 0) {
    return ESP_OK;
  }
  ResponseBody* body = static_cast<ResponseBody*>(event->user_data);
  if (body == nullptr || event->data_len < 0 ||
      static_cast<size_t>(event->data_len) >
          ResponseBody::Capacity - body->length) {
    if (body != nullptr) {
      body->overflow = true;
    }
    return ESP_FAIL;
  }
  std::memcpy(body->data + body->length, event->data,
              static_cast<size_t>(event->data_len));
  body->length += static_cast<size_t>(event->data_len);
  return ESP_OK;
}

TokenExchangeResponse EspIdfTokenExchangeTransport::ParseResponse(
    const ResponseBody& body) {
  TokenExchangeResponse response;
  ParseTokenResponse(body.data, body.length, response);
  return response;
}

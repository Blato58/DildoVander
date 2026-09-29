#pragma once

#include <stdint.h>

#include <string>

#include "esp_http_client.h"

#include "TokenExchangePort.h"

// Exchange blocks the calling task; use it only from the future network task.
class EspIdfTokenExchangeTransport final : public TokenExchangePort {
 public:
  explicit EspIdfTokenExchangeTransport(const std::string& backendHost,
                                        uint16_t port = 443);

  TokenExchangeResponse Exchange(const char* credentialHex) override;
  bool IsConfigured() const;

 private:
  struct ResponseBody {
    static constexpr size_t Capacity = 512;
    char data[Capacity] = {};
    size_t length = 0;
    bool overflow = false;
  };

  static esp_err_t OnHttpEvent(esp_http_client_event_t* event);
  static TokenExchangeResponse ParseResponse(const ResponseBody& body);

  std::string url_;
};

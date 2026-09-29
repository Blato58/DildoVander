#pragma once

#include <stdint.h>

#include <string>

#include "esp_http_client.h"

#include "ConversationStartCodec.h"

struct ConversationStartHttpResult {
  int statusCode = 0;
  ConversationStartResponse conversation;
};

// Blocking HTTPS transport. Call only from the backend network task.
class EspIdfConversationStartTransport final {
 public:
  explicit EspIdfConversationStartTransport(const std::string& backendHost,
                                            uint16_t port = 443);

  bool IsConfigured() const;
  ConversationStartHttpResult Create(const ConversationStartRequest& request,
                                     const char* accessToken);

 private:
  struct ResponseBody {
    static constexpr size_t Capacity = 512;
    char data[Capacity] = {};
    size_t length = 0;
    bool overflow = false;
  };

  static esp_err_t OnHttpEvent(esp_http_client_event_t* event);

  std::string url_;
};

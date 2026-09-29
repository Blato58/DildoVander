#pragma once

#include <stddef.h>
#include <stdint.h>

#include <string>

#include "esp_http_client.h"

#include "ConversationTextCodec.h"

struct ConversationTextHttpResult {
  int statusCode = 0;
  std::string operationId;
};

// Blocking HTTPS transport. Call only from the backend network task.
class EspIdfConversationTextTransport final {
 public:
  explicit EspIdfConversationTextTransport(const std::string& backendHost,
                                           uint16_t port = 443);

  bool IsConfigured() const;
  ConversationTextHttpResult Submit(const ConversationTextTurnRequest& request,
                                    const char* accessToken);

 private:
  struct ResponseBody {
    static constexpr size_t Capacity = 128;
    char data[Capacity] = {};
    size_t length = 0;
    bool overflow = false;
  };

  static esp_err_t OnHttpEvent(esp_http_client_event_t* event);

  std::string baseUrl_;
};

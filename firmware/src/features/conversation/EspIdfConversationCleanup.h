#pragma once
#include <cstdint>
#include <string>
#include "esp_http_client.h"
// Supported Live/Realtime session cleanup only. Retired opening/text/poll routes
// are intentionally absent. Run on the backend worker, never control.
class EspIdfConversationCleanup final {
 public:
  explicit EspIdfConversationCleanup(const std::string& host, uint16_t port = 443);
  bool IsConfigured() const;
  int Delete(const std::string& conversation, const std::string& key, const char* token);
 private:
  std::string baseUrl_;
};

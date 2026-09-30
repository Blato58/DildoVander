#pragma once

#include <stdint.h>

#include <string>

#include "esp_http_client.h"

#include "TurnPollGuard.h"

struct TurnPollHttpResult {
  int statusCode = 0;
  TurnPollDecision decision = TurnPollDecision::Discard;
  std::string reply;
  TurnProposal proposal;
  bool noSelection = false;
};

// Blocking HTTPS GET. Call only from the backend network task.
class EspIdfTurnPollTransport final {
 public:
  explicit EspIdfTurnPollTransport(const std::string& backendHost,
                                  uint16_t port = 443);

  bool IsConfigured() const;
  TurnPollHttpResult Poll(const std::string& operationId,
                          const TurnCorrelation& expected,
                          uint64_t currentGeneration,
                          uint64_t currentDeviceRevision,
                          const std::string& currentCatalogVersion,
                          uint8_t currentReadyMask,
                          uint64_t nowUtcSeconds,
                          const char* accessToken);

 private:
  struct ResponseBody {
    static constexpr size_t Capacity = 4096;
    std::string data;
    bool overflow = false;
  };

  static esp_err_t OnHttpEvent(esp_http_client_event_t* event);

  std::string baseUrl_;
};

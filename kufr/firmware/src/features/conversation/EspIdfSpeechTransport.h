#pragma once

#include <string>

#include "SpeechOutputCoordinator.h"

// Blocking network-task adapter only. Caller owns token refresh/idempotency,
// consent, verified PSRAM storage, cancellation and the eventual playback port.
class EspIdfSpeechTransport final : public SpeechTransportPort {
 public:
  explicit EspIdfSpeechTransport(const std::string& host);
  int Submit(const std::string& conversationId, const std::string& turnId,
             const std::string& idempotencyKey, const char* token,
             std::string& operationId) override;
  int Poll(const std::string& operationId, const char* token,
           SpeechOperationObservation& operation) override;
  int Release(const std::string& operationId, const std::string& idempotencyKey,
              const char* token) override;
  int Download(const std::string& operationId, const TurnCorrelation& expected,
               const SpeechOperationObservation& operation,
               const ConversationRuntimeSnapshot& snapshot,
               const ConversationRuntimeState& runtime,
               uint64_t utcSeconds, const char* token,
               SpeechAudioBuffer& buffer) override;

 private:
  int JsonRequest(const std::string& path, const std::string& body,
                  const std::string& key, const char* token,
                  std::string& response, bool release = false);
  std::string baseUrl_;
};

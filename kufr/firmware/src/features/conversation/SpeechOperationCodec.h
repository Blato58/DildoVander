#pragma once

#include <cstddef>
#include <string>

#include "TurnPollGuard.h"

struct SpeechOperationObservation {
  std::string operationId;
  TurnOperationStatus status = TurnOperationStatus::Unknown;
  TurnCorrelation correlation;
  std::string audioId;
  std::string expiresAt;
  std::string outputTarget;
};

bool BuildSpeechRequest(const std::string& turnId, std::string& body);
bool ParseSpeechOperation(const char* data, size_t length,
                          SpeechOperationObservation& observation);
// Zero means wait, terminal failure, mismatch or an expired result. A positive
// value is a conservative monotonic TTL for this exact correlated audio.
uint64_t SpeechRemainingMs(const std::string& operationId,
                           const TurnCorrelation& expected,
                           uint64_t nowUtcSeconds,
                           const SpeechOperationObservation& observation);

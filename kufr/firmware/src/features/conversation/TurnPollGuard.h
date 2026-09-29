#pragma once

#include <stdint.h>

#include <string>

enum class TurnOperationStatus : uint8_t {
  Pending,
  Running,
  Completed,
  Failed,
  Cancelled,
  Unknown,
};

enum class TurnPollDecision : uint8_t {
  Wait,
  ReplyReady,
  Terminal,
  Discard,
};

struct TurnCorrelation {
  std::string deviceId;
  std::string bootId;
  std::string conversationId;
  std::string clientTurnId;
  uint64_t generation = 0;
  uint64_t deviceRevision = 0;
};

struct TurnOperationObservation {
  std::string operationId;
  std::string kind;
  TurnOperationStatus status = TurnOperationStatus::Unknown;
  TurnCorrelation correlation;
  std::string reply;
  std::string decision;
  bool hasProposal = false;
};

// Only a matching fake text reply can become visible. Proposals need a later
// expiry/confirmation adapter and never become physical commands here.
TurnPollDecision EvaluateTurnOperation(
    const std::string& expectedOperationId,
    const TurnCorrelation& expected,
    uint64_t currentGeneration,
    uint64_t currentDeviceRevision,
    const TurnOperationObservation& observed);

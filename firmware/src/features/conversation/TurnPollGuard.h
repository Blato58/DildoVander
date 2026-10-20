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
  ProposalReady,
  Terminal,
  Discard,
};

struct TurnProposal {
  std::string proposalId;
  std::string conversationId;
  std::string turnId;
  std::string catalogVersion;
  std::string bootId;
  std::string reason;
  std::string expiresAt;
  uint64_t generation = 0;
  uint64_t deviceRevision = 0;
  uint8_t compartment = 0;
  uint64_t operationEpoch = 0;
};

struct TurnCorrelation {
  std::string deviceId;
  std::string bootId;
  std::string conversationId;
  std::string clientTurnId;
  uint64_t generation = 0;
  uint64_t deviceRevision = 0;
  uint64_t operationEpoch = 0;
};

struct TurnOperationObservation {
  std::string operationId;
  std::string kind;
  TurnOperationStatus status = TurnOperationStatus::Unknown;
  TurnCorrelation correlation;
  std::string reply;
  std::string decision;
  bool hasProposal = false;
  TurnProposal proposal;
};

// A matching reply can become visible; proposals use the stronger guard below.
TurnPollDecision EvaluateTurnOperation(
    const std::string& expectedOperationId,
    const TurnCorrelation& expected,
    uint64_t currentGeneration,
    uint64_t currentDeviceRevision,
    const TurnOperationObservation& observed,
    uint64_t currentOperationEpoch = 0);

// The proposal is data only. An autonomous presentation adapter must separately
// enforce its own safety, calibration, and actuator gates.
TurnPollDecision EvaluateTurnOperationWithProposal(
    const std::string& expectedOperationId,
    const TurnCorrelation& expected,
    uint64_t currentGeneration,
    uint64_t currentDeviceRevision,
    const std::string& currentCatalogVersion,
    uint8_t currentReadyMask,
    uint64_t nowUtcSeconds,
    const TurnOperationObservation& observed,
    uint64_t currentOperationEpoch = 0);

// Conservative monotonic TTL; subtracts one UTC second for clock granularity.
bool ParseConversationUtcExpiry(const std::string& value, uint64_t& seconds);
uint64_t ProposalRemainingMs(const TurnProposal& proposal,
                             uint64_t nowUtcSeconds);

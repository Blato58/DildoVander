#include "TurnPollGuard.h"

TurnPollDecision EvaluateTurnOperation(
    const std::string& expectedOperationId,
    const TurnCorrelation& expected,
    uint64_t currentGeneration,
    uint64_t currentDeviceRevision,
    const TurnOperationObservation& observed) {
  const TurnCorrelation& actual = observed.correlation;
  if (expectedOperationId.empty() ||
      observed.operationId != expectedOperationId ||
      observed.kind != "turn" ||
      actual.deviceId != expected.deviceId ||
      actual.bootId != expected.bootId ||
      actual.conversationId != expected.conversationId ||
      actual.clientTurnId != expected.clientTurnId ||
      actual.generation != expected.generation ||
      actual.deviceRevision != expected.deviceRevision ||
      currentGeneration != expected.generation ||
      currentDeviceRevision != expected.deviceRevision) {
    return TurnPollDecision::Discard;
  }

  switch (observed.status) {
    case TurnOperationStatus::Pending:
    case TurnOperationStatus::Running:
      return TurnPollDecision::Wait;
    case TurnOperationStatus::Failed:
    case TurnOperationStatus::Cancelled:
    case TurnOperationStatus::Unknown:
      return TurnPollDecision::Terminal;
    case TurnOperationStatus::Completed:
      if (!observed.hasProposal &&
          (observed.decision == "ask" ||
           observed.decision == "no_selection") &&
          !observed.reply.empty() && observed.reply.size() <= 2000) {
        return TurnPollDecision::ReplyReady;
      }
      return TurnPollDecision::Discard;
  }
  return TurnPollDecision::Discard;
}

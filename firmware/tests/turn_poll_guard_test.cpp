#include <cassert>

#include "../src/features/conversation/TurnPollGuard.h"

namespace {

TurnCorrelation Expected() {
  TurnCorrelation value;
  value.deviceId = "kufr-001";
  value.bootId = "c85743d5-1b72-41b8-8500-ae3c102b27d6";
  value.conversationId = "9a8dd264-61f7-4339-b677-866798263cb2";
  value.clientTurnId = "8475532e-92b2-487e-b476-7f98f0030e68";
  value.generation = 2;
  value.deviceRevision = 4;
  return value;
}

TurnOperationObservation Completed(const TurnCorrelation& expected) {
  TurnOperationObservation result;
  result.operationId = "7731ce10-a53c-4934-8909-06870fc86a12";
  result.kind = "turn";
  result.status = TurnOperationStatus::Completed;
  result.correlation = expected;
  result.reply = "Toto je testovací odpoved bez AI.";
  result.decision = "ask";
  return result;
}

TurnPollDecision Evaluate(const TurnCorrelation& expected,
                          const TurnOperationObservation& actual,
                          uint64_t generation = 2,
                          uint64_t revision = 4) {
  return EvaluateTurnOperation(
      "7731ce10-a53c-4934-8909-06870fc86a12", expected,
      generation, revision, actual);
}

}  // namespace

int main() {
  const TurnCorrelation expected = Expected();
  TurnOperationObservation actual = Completed(expected);
  assert(Evaluate(expected, actual) == TurnPollDecision::ReplyReady);
  actual.status = TurnOperationStatus::Running;
  assert(Evaluate(expected, actual) == TurnPollDecision::Wait);
  actual.status = TurnOperationStatus::Failed;
  assert(Evaluate(expected, actual) == TurnPollDecision::Terminal);
  actual = Completed(expected);
  assert(Evaluate(expected, actual, 3) == TurnPollDecision::Discard);
  assert(Evaluate(expected, actual, 2, 5) == TurnPollDecision::Discard);

  actual.correlation.deviceId = "other";
  assert(Evaluate(expected, actual) == TurnPollDecision::Discard);
  actual = Completed(expected);
  actual.correlation.bootId = "other";
  assert(Evaluate(expected, actual) == TurnPollDecision::Discard);
  actual = Completed(expected);
  actual.correlation.conversationId = "other";
  assert(Evaluate(expected, actual) == TurnPollDecision::Discard);
  actual = Completed(expected);
  actual.correlation.clientTurnId = "other";
  assert(Evaluate(expected, actual) == TurnPollDecision::Discard);
  actual = Completed(expected);
  ++actual.correlation.generation;
  assert(Evaluate(expected, actual) == TurnPollDecision::Discard);
  actual = Completed(expected);
  ++actual.correlation.deviceRevision;
  assert(Evaluate(expected, actual) == TurnPollDecision::Discard);
  actual = Completed(expected);
  actual.operationId = "other";
  assert(Evaluate(expected, actual) == TurnPollDecision::Discard);
  actual = Completed(expected);
  actual.hasProposal = true;
  actual.decision = "propose";
  assert(Evaluate(expected, actual) == TurnPollDecision::Discard);
}

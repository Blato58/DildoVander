#include <cassert>
#include <string>

#include "../src/features/conversation/TurnOperationCodec.h"
#include "../src/features/conversation/TurnPollGuard.h"

namespace {

const std::string Correlation =
    "{\"deviceId\":\"kufr-001\","
    "\"bootId\":\"c85743d5-1b72-41b8-8500-ae3c102b27d6\","
    "\"conversationId\":\"9a8dd264-61f7-4339-b677-866798263cb2\","
    "\"clientTurnId\":\"8475532e-92b2-487e-b476-7f98f0030e68\","
    "\"generation\":2,\"deviceRevision\":4}";

std::string Operation(const std::string& status,
                      const std::string& result,
                      const std::string& error = "null") {
  return "{\"operationId\":\"7731ce10-a53c-4934-8909-06870fc86a12\","
         "\"kind\":\"turn\",\"status\":\"" + status +
         "\",\"correlation\":" + Correlation +
         ",\"result\":" + result + ",\"error\":" + error + "}";
}

bool Parse(const std::string& json, TurnOperationObservation& result) {
  return ParseTurnOperation(json.data(), json.size(), result);
}

}  // namespace

int main() {
  TurnOperationObservation parsed;
  const std::string completed = Operation(
      "completed",
      "{\"reply\":\"Toto je testovac\\u00ed odpov\\u011b\\u010f.\","
      "\"decision\":\"ask\",\"reason\":\"\",\"proposal\":null}");
  assert(Parse(completed, parsed));
  assert(parsed.reply == u8"Toto je testovací odpověď.");
  assert(parsed.status == TurnOperationStatus::Completed);
  assert(parsed.correlation.generation == 2);
  assert(parsed.correlation.deviceRevision == 4);
  assert(EvaluateTurnOperation(parsed.operationId, parsed.correlation, 2, 4,
                               parsed) == TurnPollDecision::ReplyReady);
  assert(EvaluateTurnOperation(parsed.operationId, parsed.correlation, 3, 4,
                               parsed) == TurnPollDecision::Discard);
  assert(Parse(Operation("pending", "null"), parsed));
  assert(parsed.status == TurnOperationStatus::Pending);
  assert(parsed.reply.empty());
  assert(Parse(Operation("failed", "null", "{\"code\":\"quota\"}"),
               parsed));
  assert(parsed.status == TurnOperationStatus::Failed);

  assert(!Parse(Operation("completed", "null"), parsed));
  assert(parsed.operationId.empty());
  assert(!Parse(Operation("failed", "null"), parsed));
  assert(!Parse(Operation("completed",
                          "{\"reply\":\"a\",\"decision\":\"propose\","
                          "\"reason\":\"\",\"proposal\":{}}"),
                parsed));
  const std::string proposal =
      "{\"proposalId\":\"6c37b199-0cce-48ca-9ca9-26cff6710088\","
      "\"conversationId\":\"9a8dd264-61f7-4339-b677-866798263cb2\","
      "\"turnId\":\"8475532e-92b2-487e-b476-7f98f0030e68\","
      "\"catalogVersion\":\"neutral-1\",\"deviceRevision\":4,"
      "\"compartment\":3,\"reason\":\"fits\","
      "\"bootId\":\"c85743d5-1b72-41b8-8500-ae3c102b27d6\","
      "\"generation\":2,\"expiresAt\":\"2030-01-01T00:00:30+00:00\"}";
  const std::string proposed = Operation(
      "completed", "{\"reply\":\"Tvoje hulka ceka.\","
                   "\"decision\":\"propose\",\"reason\":\"fits\","
                   "\"proposal\":" + proposal + "}");
  assert(Parse(proposed, parsed));
  assert(parsed.hasProposal && parsed.proposal.compartment == 3);
  assert(EvaluateTurnOperationWithProposal(
      parsed.operationId, parsed.correlation, 2, 4, "neutral-1", 0x3f,
      1893456000, parsed) == TurnPollDecision::ProposalReady);
  assert(ProposalRemainingMs(parsed.proposal, 1893456000) == 29000);
  TurnOperationObservation changed = parsed;
  changed.proposal.bootId = "c85743d5-1b72-41b8-8500-ae3c102b27d7";
  assert(EvaluateTurnOperationWithProposal(
      parsed.operationId, parsed.correlation, 2, 4, "neutral-1", 0x3f,
      1893456000, changed) == TurnPollDecision::Discard);
  changed = parsed;
  ++changed.proposal.generation;
  assert(EvaluateTurnOperationWithProposal(
      parsed.operationId, parsed.correlation, 2, 4, "neutral-1", 0x3f,
      1893456000, changed) == TurnPollDecision::Discard);
  changed = parsed;
  ++changed.proposal.deviceRevision;
  assert(EvaluateTurnOperationWithProposal(
      parsed.operationId, parsed.correlation, 2, 4, "neutral-1", 0x3f,
      1893456000, changed) == TurnPollDecision::Discard);
  changed = parsed;
  changed.proposal.conversationId = "6c37b199-0cce-48ca-9ca9-26cff6710088";
  assert(EvaluateTurnOperationWithProposal(
      parsed.operationId, parsed.correlation, 2, 4, "neutral-1", 0x3f,
      1893456000, changed) == TurnPollDecision::Discard);
  assert(EvaluateTurnOperationWithProposal(
      parsed.operationId, parsed.correlation, 2, 4, "neutral-1", 0x3b,
      1893456000, parsed) == TurnPollDecision::Discard);
  assert(EvaluateTurnOperationWithProposal(
      parsed.operationId, parsed.correlation, 2, 4, "other", 0x3f,
      1893456000, parsed) == TurnPollDecision::Discard);
  assert(EvaluateTurnOperationWithProposal(
      parsed.operationId, parsed.correlation, 2, 4, "neutral-1", 0x3f,
      1893456030, parsed) == TurnPollDecision::Discard);
  std::string wrongProposal = proposed;
  size_t wrongPosition = wrongProposal.find("\"compartment\":3");
  assert(wrongPosition != std::string::npos);
  wrongProposal.replace(wrongPosition, 15, "\"compartment\":7");
  assert(!Parse(wrongProposal, parsed));
  wrongProposal = proposed;
  wrongPosition = wrongProposal.find("\"turnId\":\"8475532e");
  assert(wrongPosition != std::string::npos);
  wrongProposal.replace(wrongPosition + 10, 1, "9");
  assert(Parse(wrongProposal, parsed));
  assert(EvaluateTurnOperationWithProposal(
      parsed.operationId, parsed.correlation, 2, 4, "neutral-1", 0x3f,
      1893456000, parsed) == TurnPollDecision::Discard);
  assert(!Parse(Operation("completed",
                          "{\"reply\":\"a\",\"decision\":\"ask\","
                          "\"reason\":\"\",\"proposal\":null,"
                          "\"reply\":\"b\"}"),
                parsed));
  assert(!Parse(Operation("pending", "null") + "{}", parsed));
  const std::string oversizedReply = Operation(
      "completed", "{\"reply\":\"" + std::string(501, 'a') +
                       "\",\"decision\":\"ask\",\"reason\":\"\","
                       "\"proposal\":null}");
  assert(!Parse(oversizedReply, parsed));
  const std::string maximumReply = Operation(
      "completed", "{\"reply\":\"" + std::string(500, 'a') +
                       "\",\"decision\":\"ask\",\"reason\":\"\","
                       "\"proposal\":null}");
  assert(Parse(maximumReply, parsed));
  const std::string invalidReason = Operation(
      "completed", "{\"reply\":\"a\",\"decision\":\"ask\","
                       "\"reason\":\"bad\xc0\xaf\",\"proposal\":null}");
  assert(!Parse(invalidReason, parsed));
  const std::string wrongCorrelation = Operation("pending", "null");
  std::string modified = wrongCorrelation;
  const size_t position = modified.find("\"deviceRevision\":4");
  assert(position != std::string::npos);
  modified.replace(position, 18, "\"deviceRevision\":-1");
  assert(!Parse(modified, parsed));
  assert(!Parse(std::string(4097, 'x'), parsed));
}

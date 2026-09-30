#include <cassert>
#include <string>

#include "../src/features/conversation/ConversationReplyInbox.h"
#include "../src/features/conversation/ConversationSession.h"
#include "../src/features/conversation/ConversationStartCodec.h"
#include "../src/features/conversation/ConversationTextInbox.h"
#include "../src/features/conversation/TurnPollGuard.h"

namespace {

constexpr char LocalTurnId[] = "AD64D2AC-D76A-4E5D-9CE7-A0B468F31C85";
constexpr char BackendTurnId[] = "ad64d2ac-d76a-4e5d-9ce7-a0b468f31c85";
constexpr char LocalConversationId[] = "F1E83B7C-DF66-48DF-B0F8-6C7B215B38B1";
constexpr char BackendConversationId[] = "b78ea5a1-6c11-426a-9b6a-3293612248d3";

void TestLocalTurnSurvivesBackendCorrelation() {
  ConversationRuntimeState runtime(true, 63, "catalog-1");
  ConversationRuntimeSnapshot snapshot;
  assert(runtime.Read(snapshot));

  ConversationSession session("boot-1", LocalConversationId);
  ConversationTextCommand command;
  command.bootId = "boot-1";
  command.conversationId = LocalConversationId;
  command.clientTurnId = LocalTurnId;
  command.expectedRevision = snapshot.deviceRevision;
  command.text = "Which wand?";
  assert(session.SubmitText(command, true, snapshot.deviceRevision).error ==
         ConversationCommandError::None);

  ConversationTextInbox textInbox;
  assert(textInbox.Submit(
      command.text, command.conversationId, command.clientTurnId,
      snapshot, runtime));
  std::string queuedText;
  std::string queuedConversationId;
  std::string queuedTurnId;
  uint64_t snapshotVersion = 0;
  assert(textInbox.Take(
      queuedText, queuedConversationId, queuedTurnId, snapshotVersion));
  assert(queuedText == command.text);
  assert(queuedConversationId == LocalConversationId);
  assert(queuedTurnId == LocalTurnId);
  assert(snapshotVersion == snapshot.version);

  std::string backendTurnId;
  assert(NormalizeConversationUuid(queuedTurnId, backendTurnId));
  assert(backendTurnId == BackendTurnId);
  TurnCorrelation expected;
  expected.deviceId = "device-1";
  expected.bootId = "boot-1";
  expected.conversationId = BackendConversationId;
  expected.clientTurnId = backendTurnId;
  expected.generation = snapshot.generation;
  expected.deviceRevision = snapshot.deviceRevision;
  TurnOperationObservation observed;
  observed.operationId = "operation-1";
  observed.kind = "turn";
  observed.status = TurnOperationStatus::Completed;
  observed.correlation = expected;
  observed.reply = "This one.";
  observed.decision = "ask";
  assert(EvaluateTurnOperation(
             observed.operationId, expected, snapshot.generation,
             snapshot.deviceRevision, observed) ==
         TurnPollDecision::ReplyReady);
  observed.correlation.clientTurnId = queuedTurnId;
  assert(EvaluateTurnOperation(
             observed.operationId, expected, snapshot.generation,
             snapshot.deviceRevision, observed) ==
         TurnPollDecision::Discard);

  ConversationReplyInbox replyInbox;
  assert(replyInbox.Store(
      "This one.", queuedConversationId, queuedTurnId, snapshot, runtime));
  std::string reply;
  std::string resultConversationId;
  std::string resultTurnId;
  ConversationRuntimeSnapshot resultSnapshot;
  uint64_t token = 0;
  assert(replyInbox.Take(
      reply, resultConversationId, resultTurnId, resultSnapshot,
      token, runtime));
  assert(reply == "This one.");
  assert(resultConversationId == LocalConversationId);
  assert(resultTurnId == LocalTurnId);
  assert(!session.CompleteTurn(backendTurnId, session.Generation()));
  assert(session.CompleteTurn(resultTurnId, session.Generation()));
  assert(replyInbox.Complete(token));
}

}  // namespace

int main() {
  TestLocalTurnSurvivesBackendCorrelation();
}

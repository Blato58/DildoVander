#include <cassert>
#include <cstdint>
#include <string>

#include "../src/features/conversation/ConversationReplyInbox.h"
#include "../src/features/conversation/ConversationSession.h"

namespace {

constexpr char FirstTurnId[] = "8475532e-92b2-487e-b476-7f98f0030e68";
constexpr char ConversationId[] = "f1e83b7c-df66-48df-b0f8-6c7b215b38b1";
constexpr char SecondTurnId[] = "ad64d2ac-d76a-4e5d-9ce7-a0b468f31c85";
constexpr char UpperTurnId[] = "AD64D2AC-D76A-4E5D-9CE7-A0B468F31C85";
constexpr char NilTurnId[] = "00000000-0000-0000-0000-000000000000";

void TestReplyBelongsToOneSnapshot() {
  ConversationRuntimeState runtime(true, 63, "catalog-1");
  ConversationRuntimeSnapshot initial;
  assert(runtime.Read(initial));
  ConversationReplyInbox inbox;

  assert(inbox.Store("Your wand is ready.", ConversationId, FirstTurnId, initial, runtime));
  assert(inbox.HasPending(runtime));
  assert(!inbox.Store("Another reply", ConversationId, SecondTurnId, initial, runtime));

  std::string reply;
  std::string conversationId;
  std::string clientTurnId;
  ConversationRuntimeSnapshot taken;
  uint64_t token = 0;
  assert(inbox.Take(reply, conversationId, clientTurnId, taken, token, runtime));
  assert(reply == "Your wand is ready.");
  assert(conversationId == ConversationId);
  assert(clientTurnId == FirstTurnId);
  assert(runtime.Matches(taken));
  assert(inbox.IsCurrent(token, taken, runtime));
  assert(inbox.HasPending(runtime));
  assert(!inbox.Store("Another reply", ConversationId, SecondTurnId, initial, runtime));
  assert(!inbox.Take(reply, conversationId, clientTurnId, taken, token, runtime));
  assert(inbox.Complete(token));
  assert(!inbox.HasPending(runtime));
  assert(!inbox.IsCurrent(token, taken, runtime));
  assert(!inbox.Complete(token));
}

void TestStaleAndCancelledRepliesAreDropped() {
  ConversationRuntimeState runtime(true, 63, "catalog-1");
  ConversationRuntimeSnapshot initial;
  assert(runtime.Read(initial));
  ConversationReplyInbox inbox;
  assert(inbox.Store("Stale reply", ConversationId, FirstTurnId, initial, runtime));
  assert(runtime.Publish(1, 1, 63, "catalog-1"));
  std::string reply;
  std::string conversationId;
  std::string clientTurnId;
  ConversationRuntimeSnapshot taken;
  uint64_t token = 0;
  assert(!inbox.HasPending(runtime));
  assert(!inbox.Take(reply, conversationId, clientTurnId, taken, token, runtime));
  assert(!inbox.Store("Too late", ConversationId, FirstTurnId, initial, runtime));

  ConversationRuntimeSnapshot current;
  assert(runtime.Read(current));
  assert(inbox.Store("Cancel", ConversationId, SecondTurnId, current, runtime));
  assert(inbox.Take(reply, conversationId, clientTurnId, taken, token, runtime));
  assert(clientTurnId == SecondTurnId);
  assert(inbox.IsCurrent(token, taken, runtime));
  assert(runtime.Publish(1, 2, 63, "catalog-1"));
  assert(!inbox.IsCurrent(token, taken, runtime));
  assert(!inbox.HasPending(runtime));
  assert(!inbox.Complete(token));

  assert(runtime.Read(current));
  assert(inbox.Store("Cancel", ConversationId, FirstTurnId, current, runtime));
  assert(inbox.Take(reply, conversationId, clientTurnId, taken, token, runtime));
  inbox.Clear();
  assert(!inbox.IsCurrent(token, taken, runtime));
  assert(!inbox.Complete(token));
  assert(!inbox.HasPending(runtime));
  assert(!inbox.Take(reply, conversationId, clientTurnId, taken, token, runtime));
}

void TestReplyCompletesOnlyMatchingSessionTurn() {
  ConversationRuntimeState runtime(true, 63, "catalog-1");
  ConversationRuntimeSnapshot snapshot;
  assert(runtime.Read(snapshot));
  ConversationSession session("boot-1", "conversation-1");
  ConversationTextCommand command;
  command.bootId = "boot-1";
  command.conversationId = "conversation-1";
  command.clientTurnId = UpperTurnId;
  command.expectedRevision = snapshot.deviceRevision;
  command.text = "Which wand?";
  assert(session.SubmitText(command, true, snapshot.deviceRevision).error ==
         ConversationCommandError::None);

  ConversationReplyInbox inbox;
  assert(inbox.Store("This one.", ConversationId, command.clientTurnId, snapshot, runtime));
  std::string reply;
  std::string conversationId;
  std::string clientTurnId;
  ConversationRuntimeSnapshot taken;
  uint64_t token = 0;
  assert(inbox.Take(reply, conversationId, clientTurnId, taken, token, runtime));
  assert(!session.CompleteTurn(SecondTurnId, session.Generation()));
  assert(session.HasActiveTurn());
  assert(session.CompleteTurn(clientTurnId, session.Generation()));
  assert(inbox.Complete(token));
}

void TestBoundsAndInvalidText() {
  ConversationRuntimeState runtime(true, 63, "catalog-1");
  ConversationRuntimeSnapshot snapshot;
  assert(runtime.Read(snapshot));
  ConversationReplyInbox inbox;
  assert(!inbox.Store("", ConversationId, FirstTurnId, snapshot, runtime));
  assert(!inbox.Store(std::string(501, 'x'), ConversationId, FirstTurnId, snapshot, runtime));
  assert(!inbox.Store(
      std::string("\xc3\x28", 2), ConversationId, FirstTurnId, snapshot, runtime));
  assert(!inbox.Store("Valid", ConversationId, "invalid-id", snapshot, runtime));
  assert(!inbox.Store("Valid", ConversationId, NilTurnId, snapshot, runtime));
  assert(!inbox.Store("Valid", NilTurnId, FirstTurnId, snapshot, runtime));
  assert(!inbox.HasPending(runtime));
  assert(inbox.Store(std::string(500, 'x'), ConversationId, FirstTurnId, snapshot, runtime));
  assert(inbox.HasPending(runtime));
  inbox.Clear();

  std::string fourByteCharacter("\xf0\x9f\x98\x80", 4);
  std::string maxUtf8Reply;
  for (int index = 0; index < 500; ++index) {
    maxUtf8Reply += fourByteCharacter;
  }
  assert(maxUtf8Reply.size() == 2000);
  assert(inbox.Store(maxUtf8Reply, ConversationId, SecondTurnId, snapshot, runtime));
  inbox.Clear();
  assert(!inbox.Store(
      maxUtf8Reply + fourByteCharacter, ConversationId, FirstTurnId, snapshot, runtime));
}

}  // namespace

int main() {
  TestReplyBelongsToOneSnapshot();
  TestStaleAndCancelledRepliesAreDropped();
  TestReplyCompletesOnlyMatchingSessionTurn();
  TestBoundsAndInvalidText();
}

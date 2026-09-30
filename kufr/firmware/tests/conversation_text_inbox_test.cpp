#include <cassert>
#include <string>

#include "../src/features/conversation/ConversationTextInbox.h"
#include "../src/features/conversation/ConversationStartCodec.h"

namespace {

constexpr char FirstTurnId[] = "8475532e-92b2-487e-b476-7f98f0030e68";
constexpr char ConversationId[] = "f1e83b7c-df66-48df-b0f8-6c7b215b38b1";
constexpr char NextConversationId[] = "b78ea5a1-6c11-426a-9b6a-3293612248d3";
constexpr char SecondTurnId[] = "ad64d2ac-d76a-4e5d-9ce7-a0b468f31c85";
constexpr char UpperTurnId[] = "AD64D2AC-D76A-4E5D-9CE7-A0B468F31C85";
constexpr char NilTurnId[] = "00000000-0000-0000-0000-000000000000";

}  // namespace

int main() {
  ConversationRuntimeState state(true, 63, "neutral-1");
  ConversationRuntimeSnapshot snapshot;
  assert(state.Read(snapshot));
  ConversationTextInbox inbox;
  std::string text;
  std::string conversationId;
  std::string clientTurnId;
  uint64_t snapshotVersion = 0;
  assert(!inbox.Submit("", ConversationId, FirstTurnId, snapshot, state));
  assert(!inbox.Submit(" \t\n", ConversationId, FirstTurnId, snapshot, state));
  assert(!inbox.Submit(std::string(4001, 'a'), ConversationId, FirstTurnId, snapshot, state));
  assert(!inbox.Submit("\xc0\xaf", ConversationId, FirstTurnId, snapshot, state));
  assert(!inbox.Submit("Valid text", ConversationId, "invalid-id", snapshot, state));
  assert(!inbox.Submit("Valid text", ConversationId, NilTurnId, snapshot, state));
  assert(!inbox.Submit("Valid text", NilTurnId, FirstTurnId, snapshot, state));
  ConversationRuntimeSnapshot invalidSnapshot = snapshot;
  invalidSnapshot.version = 0;
  assert(!inbox.Submit("Prvni tah", ConversationId, FirstTurnId, invalidSnapshot, state));
  assert(inbox.Submit("Prvni tah", ConversationId, FirstTurnId, snapshot, state));
  assert(!inbox.Submit("Druhy tah", ConversationId, SecondTurnId, snapshot, state));
  assert(inbox.Take(text, conversationId, clientTurnId, snapshotVersion));
  assert(text == "Prvni tah");
  assert(conversationId == ConversationId);
  assert(clientTurnId == FirstTurnId);
  assert(snapshotVersion == snapshot.version);
  assert(!inbox.Take(text, conversationId, clientTurnId, snapshotVersion));

  assert(state.Publish(1, 0, 63, "neutral-1"));
  assert(!inbox.Submit("Stary tah", ConversationId, FirstTurnId, snapshot, state));
  assert(state.Read(snapshot));
  assert(inbox.Submit("Po odpojeni zahodit", ConversationId, SecondTurnId, snapshot, state));
  inbox.Clear();
  assert(!inbox.Take(text, conversationId, clientTurnId, snapshotVersion));
  state.Invalidate();
  assert(!inbox.Submit("Bez stavu", ConversationId, FirstTurnId, snapshot, state));
  assert(state.Publish(2, 0, 63, "neutral-1"));
  assert(state.Read(snapshot));
  std::string normalizedTurnId;
  assert(NormalizeConversationUuid(UpperTurnId, normalizedTurnId));
  assert(normalizedTurnId == SecondTurnId);
  assert(inbox.Submit("Novy tah", ConversationId, UpperTurnId, snapshot, state));
  assert(inbox.Take(text, conversationId, clientTurnId, snapshotVersion));
  assert(text == "Novy tah");
  assert(clientTurnId == UpperTurnId);
  assert(snapshotVersion == snapshot.version);

  assert(inbox.Submit("Old session", ConversationId, FirstTurnId, snapshot, state));
  inbox.DiscardUnless(NextConversationId);
  assert(!inbox.Take(text, conversationId, clientTurnId, snapshotVersion));
  assert(inbox.Submit("New session", NextConversationId, SecondTurnId,
                      snapshot, state));
  inbox.DiscardUnless(NextConversationId);
  assert(inbox.Take(text, conversationId, clientTurnId, snapshotVersion));
  assert(conversationId == NextConversationId);
  assert(text == "New session");
}

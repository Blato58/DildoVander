#include <cassert>
#include <string>

#include "../src/features/conversation/ConversationTextInbox.h"
#include "../src/features/conversation/ConversationStartCodec.h"

namespace {

constexpr char FirstTurnId[] = "8475532e-92b2-487e-b476-7f98f0030e68";
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
  std::string clientTurnId;
  uint64_t snapshotVersion = 0;
  assert(!inbox.Submit("", FirstTurnId, snapshot, state));
  assert(!inbox.Submit(" \t\n", FirstTurnId, snapshot, state));
  assert(!inbox.Submit(std::string(4001, 'a'), FirstTurnId, snapshot, state));
  assert(!inbox.Submit("\xc0\xaf", FirstTurnId, snapshot, state));
  assert(!inbox.Submit("Valid text", "invalid-id", snapshot, state));
  assert(!inbox.Submit("Valid text", NilTurnId, snapshot, state));
  ConversationRuntimeSnapshot invalidSnapshot = snapshot;
  invalidSnapshot.version = 0;
  assert(!inbox.Submit("Prvni tah", FirstTurnId, invalidSnapshot, state));
  assert(inbox.Submit("Prvni tah", FirstTurnId, snapshot, state));
  assert(!inbox.Submit("Druhy tah", SecondTurnId, snapshot, state));
  assert(inbox.Take(text, clientTurnId, snapshotVersion));
  assert(text == "Prvni tah");
  assert(clientTurnId == FirstTurnId);
  assert(snapshotVersion == snapshot.version);
  assert(!inbox.Take(text, clientTurnId, snapshotVersion));

  assert(state.Publish(1, 0, 63, "neutral-1"));
  assert(!inbox.Submit("Stary tah", FirstTurnId, snapshot, state));
  assert(state.Read(snapshot));
  assert(inbox.Submit("Po odpojeni zahodit", SecondTurnId, snapshot, state));
  inbox.Clear();
  assert(!inbox.Take(text, clientTurnId, snapshotVersion));
  state.Invalidate();
  assert(!inbox.Submit("Bez stavu", FirstTurnId, snapshot, state));
  assert(state.Publish(2, 0, 63, "neutral-1"));
  assert(state.Read(snapshot));
  std::string normalizedTurnId;
  assert(NormalizeConversationUuid(UpperTurnId, normalizedTurnId));
  assert(normalizedTurnId == SecondTurnId);
  assert(inbox.Submit("Novy tah", UpperTurnId, snapshot, state));
  assert(inbox.Take(text, clientTurnId, snapshotVersion));
  assert(text == "Novy tah");
  assert(clientTurnId == UpperTurnId);
  assert(snapshotVersion == snapshot.version);
}

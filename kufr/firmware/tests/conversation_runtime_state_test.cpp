#include <cassert>

#include "../src/features/conversation/ConversationRuntimeState.h"
#include "../src/features/conversation/ConversationTextInbox.h"

namespace {

constexpr char ClientTurnId[] = "8475532e-92b2-487e-b476-7f98f0030e68";

}  // namespace

int main() {
  ConversationRuntimeState unavailable(false, 63, "neutral-1");
  ConversationRuntimeSnapshot snapshot;
  assert(!unavailable.Read(snapshot));
  assert(!unavailable.Publish(0, 0, 64, "neutral-1"));
  assert(!unavailable.Publish(0, 0, 63, "bad\"catalog"));
  assert(unavailable.Publish(0, 0, 63, "neutral-1"));
  assert(unavailable.Read(snapshot));
  assert(snapshot.version == 1);

  ConversationRuntimeState state(true, 63, "neutral-1");
  assert(state.Read(snapshot));
  assert(snapshot.generation == 0);
  assert(snapshot.deviceRevision == 0);
  assert(snapshot.readyMask == 63);
  assert(snapshot.catalogVersion == "neutral-1");
  assert(state.Matches(snapshot));

  ConversationTextInbox inbox;
  assert(inbox.Submit("Stary tah", ClientTurnId, ClientTurnId, snapshot, state));

  assert(!state.Publish(0, 0, 62, "neutral-1"));
  assert(!state.Publish(0, 0, 63, "other"));
  assert(state.Matches(snapshot));
  assert(state.Publish(1, 0, 63, "neutral-1"));
  assert(!state.Matches(snapshot));
  assert(!inbox.Submit("Pozdni tah", ClientTurnId, ClientTurnId, snapshot, state));
  std::string queuedText;
  std::string queuedConversationId;
  std::string queuedTurnId;
  uint64_t queuedVersion = 0;
  assert(inbox.Take(queuedText, queuedConversationId, queuedTurnId, queuedVersion));
  assert(queuedTurnId == ClientTurnId);
  assert(queuedVersion == snapshot.version);
  assert(queuedVersion != 0);
  assert(state.Read(snapshot));
  assert(snapshot.generation == 1);

  assert(state.Publish(1, 1, 62, "neutral-2"));
  assert(!state.Matches(snapshot));
  assert(state.Read(snapshot));
  assert(snapshot.readyMask == 62);
  assert(snapshot.catalogVersion == "neutral-2");
  assert(!state.Publish(0, 2, 62, "neutral-2"));
  assert(!state.Publish(1, 0, 62, "neutral-2"));

  state.Invalidate();
  assert(!state.Read(snapshot));
  assert(!state.Matches(snapshot));
  assert(!state.Publish(0, 2, 62, "neutral-2"));
  assert(state.Publish(2, 2, 62, "neutral-2"));
  assert(state.Read(snapshot));
  assert(snapshot.generation == 2);
  assert(snapshot.deviceRevision == 2);
}

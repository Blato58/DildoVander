#include <cassert>
#include <string>

#include "../src/features/conversation/ConversationTextInbox.h"

int main() {
  ConversationRuntimeState state(true, 63, "neutral-1");
  ConversationRuntimeSnapshot snapshot;
  assert(state.Read(snapshot));
  ConversationTextInbox inbox;
  std::string text;
  uint64_t snapshotVersion = 0;
  assert(!inbox.Submit("", snapshot, state));
  assert(!inbox.Submit(" \t\n", snapshot, state));
  assert(!inbox.Submit(std::string(4001, 'a'), snapshot, state));
  assert(!inbox.Submit("\xc0\xaf", snapshot, state));
  ConversationRuntimeSnapshot invalidSnapshot = snapshot;
  invalidSnapshot.version = 0;
  assert(!inbox.Submit("Prvni tah", invalidSnapshot, state));
  assert(inbox.Submit("Prvni tah", snapshot, state));
  assert(!inbox.Submit("Druhy tah", snapshot, state));
  assert(inbox.Take(text, snapshotVersion));
  assert(text == "Prvni tah");
  assert(snapshotVersion == snapshot.version);
  assert(!inbox.Take(text, snapshotVersion));

  assert(state.Publish(1, 0, 63, "neutral-1"));
  assert(!inbox.Submit("Stary tah", snapshot, state));
  assert(state.Read(snapshot));
  assert(inbox.Submit("Po odpojeni zahodit", snapshot, state));
  inbox.Clear();
  assert(!inbox.Take(text, snapshotVersion));
  state.Invalidate();
  assert(!inbox.Submit("Bez stavu", snapshot, state));
  assert(state.Publish(2, 0, 63, "neutral-1"));
  assert(state.Read(snapshot));
  assert(inbox.Submit("Novy tah", snapshot, state));
  assert(inbox.Take(text, snapshotVersion));
  assert(text == "Novy tah");
  assert(snapshotVersion == snapshot.version);
}

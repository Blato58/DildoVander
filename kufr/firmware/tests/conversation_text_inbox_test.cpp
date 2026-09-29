#include <cassert>
#include <string>

#include "../src/features/conversation/ConversationTextInbox.h"

int main() {
  ConversationTextInbox inbox;
  std::string text;
  uint64_t snapshotVersion = 0;
  assert(!inbox.Submit("", 1));
  assert(!inbox.Submit(" \t\n", 1));
  assert(!inbox.Submit(std::string(4001, 'a'), 1));
  assert(!inbox.Submit("\xc0\xaf", 1));
  assert(!inbox.Submit("Prvni tah", 0));
  assert(inbox.Submit("Prvni tah", 1));
  assert(!inbox.Submit("Druhy tah", 1));
  assert(inbox.Take(text, snapshotVersion));
  assert(text == "Prvni tah");
  assert(snapshotVersion == 1);
  assert(!inbox.Take(text, snapshotVersion));
  assert(inbox.Submit("Po odpojeni zahodit", 2));
  inbox.Clear();
  assert(!inbox.Take(text, snapshotVersion));
  assert(inbox.Submit("Novy tah", 3));
  assert(inbox.Take(text, snapshotVersion));
  assert(text == "Novy tah");
  assert(snapshotVersion == 3);
}

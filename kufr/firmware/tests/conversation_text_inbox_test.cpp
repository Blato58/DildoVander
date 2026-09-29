#include <cassert>
#include <string>

#include "../src/features/conversation/ConversationTextInbox.h"

int main() {
  ConversationTextInbox inbox;
  std::string text;
  assert(!inbox.Submit(""));
  assert(!inbox.Submit(" \t\n"));
  assert(!inbox.Submit(std::string(4001, 'a')));
  assert(!inbox.Submit("\xc0\xaf"));
  assert(inbox.Submit("Prvni tah"));
  assert(!inbox.Submit("Druhy tah"));
  assert(inbox.Take(text));
  assert(text == "Prvni tah");
  assert(!inbox.Take(text));
  assert(inbox.Submit("Po odpojeni zahodit"));
  inbox.Clear();
  assert(!inbox.Take(text));
  assert(inbox.Submit("Novy tah"));
  assert(inbox.Take(text));
  assert(text == "Novy tah");
}

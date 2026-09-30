#include <cassert>
#include <cstdint>
#include <string>

#include "../src/features/conversation/ConversationReplyInbox.h"

namespace {

void TestReplyBelongsToOneSnapshot() {
  ConversationRuntimeState runtime(true, 63, "catalog-1");
  ConversationRuntimeSnapshot initial;
  assert(runtime.Read(initial));
  ConversationReplyInbox inbox;

  assert(inbox.Store("Your wand is ready.", initial, runtime));
  assert(inbox.HasPending(runtime));
  assert(!inbox.Store("Another reply", initial, runtime));

  std::string reply;
  ConversationRuntimeSnapshot taken;
  uint64_t token = 0;
  assert(inbox.Take(reply, taken, token, runtime));
  assert(reply == "Your wand is ready.");
  assert(runtime.Matches(taken));
  assert(inbox.IsCurrent(token, taken, runtime));
  assert(inbox.HasPending(runtime));
  assert(!inbox.Store("Another reply", initial, runtime));
  assert(!inbox.Take(reply, taken, token, runtime));
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
  assert(inbox.Store("Stale reply", initial, runtime));
  assert(runtime.Publish(1, 1, 63, "catalog-1"));
  std::string reply;
  ConversationRuntimeSnapshot taken;
  uint64_t token = 0;
  assert(!inbox.HasPending(runtime));
  assert(!inbox.Take(reply, taken, token, runtime));
  assert(!inbox.Store("Too late", initial, runtime));

  ConversationRuntimeSnapshot current;
  assert(runtime.Read(current));
  assert(inbox.Store("Cancel", current, runtime));
  assert(inbox.Take(reply, taken, token, runtime));
  assert(inbox.IsCurrent(token, taken, runtime));
  assert(runtime.Publish(1, 2, 63, "catalog-1"));
  assert(!inbox.IsCurrent(token, taken, runtime));
  assert(!inbox.HasPending(runtime));
  assert(!inbox.Complete(token));

  assert(runtime.Read(current));
  assert(inbox.Store("Cancel", current, runtime));
  assert(inbox.Take(reply, taken, token, runtime));
  inbox.Clear();
  assert(!inbox.IsCurrent(token, taken, runtime));
  assert(!inbox.Complete(token));
  assert(!inbox.HasPending(runtime));
  assert(!inbox.Take(reply, taken, token, runtime));
}

void TestBoundsAndInvalidText() {
  ConversationRuntimeState runtime(true, 63, "catalog-1");
  ConversationRuntimeSnapshot snapshot;
  assert(runtime.Read(snapshot));
  ConversationReplyInbox inbox;
  assert(!inbox.Store("", snapshot, runtime));
  assert(!inbox.Store(std::string(501, 'x'), snapshot, runtime));
  assert(!inbox.Store(std::string("\xc3\x28", 2), snapshot, runtime));
  assert(!inbox.HasPending(runtime));
  assert(inbox.Store(std::string(500, 'x'), snapshot, runtime));
  assert(inbox.HasPending(runtime));
  inbox.Clear();

  std::string fourByteCharacter("\xf0\x9f\x98\x80", 4);
  std::string maxUtf8Reply;
  for (int index = 0; index < 500; ++index) {
    maxUtf8Reply += fourByteCharacter;
  }
  assert(maxUtf8Reply.size() == 2000);
  assert(inbox.Store(maxUtf8Reply, snapshot, runtime));
  inbox.Clear();
  assert(!inbox.Store(maxUtf8Reply + fourByteCharacter, snapshot, runtime));
}

}  // namespace

int main() {
  TestReplyBelongsToOneSnapshot();
  TestStaleAndCancelledRepliesAreDropped();
  TestBoundsAndInvalidText();
}

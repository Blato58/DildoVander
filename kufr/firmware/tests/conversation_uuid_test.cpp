#include <cassert>
#include <cstdint>

#include "../src/features/conversation/ConversationStartCodec.h"
#include "../src/features/conversation/ConversationUuid.h"

int main() {
  const uint8_t zero[16] = {};
  const std::string first = FormatConversationUuidV4(zero);
  assert(first == "00000000-0000-4000-8000-000000000000");
  assert(IsConversationUuid(first));

  uint8_t ones[16];
  for (uint8_t& value : ones) {
    value = 0xff;
  }
  const std::string second = FormatConversationUuidV4(ones);
  assert(second == "ffffffff-ffff-4fff-bfff-ffffffffffff");
  assert(IsConversationUuid(second));
  assert(first != second);
  assert(FormatConversationUuidV4(nullptr).empty());
}

#include "ConversationUuid.h"

#include <cstring>

std::string FormatConversationUuidV4(const uint8_t entropy[16]) {
  if (entropy == nullptr) {
    return {};
  }
  uint8_t bytes[16];
  std::memcpy(bytes, entropy, sizeof(bytes));
  bytes[6] = static_cast<uint8_t>((bytes[6] & 0x0f) | 0x40);
  bytes[8] = static_cast<uint8_t>((bytes[8] & 0x3f) | 0x80);

  constexpr char Hex[] = "0123456789abcdef";
  std::string result;
  result.reserve(36);
  for (size_t index = 0; index < sizeof(bytes); ++index) {
    if (index == 4 || index == 6 || index == 8 || index == 10) {
      result.push_back('-');
    }
    result.push_back(Hex[bytes[index] >> 4]);
    result.push_back(Hex[bytes[index] & 0x0f]);
  }
  std::memset(bytes, 0, sizeof(bytes));
  return result;
}

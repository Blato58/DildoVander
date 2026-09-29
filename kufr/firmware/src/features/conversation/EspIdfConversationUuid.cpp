#include "EspIdfConversationUuid.h"

#include <cstdint>
#include <cstring>

#include "esp_random.h"

#include "ConversationUuid.h"

std::string GenerateEspIdfConversationUuid() {
  uint8_t entropy[16];
  esp_fill_random(entropy, sizeof(entropy));
  std::string result = FormatConversationUuidV4(entropy);
  std::memset(entropy, 0, sizeof(entropy));
  return result;
}

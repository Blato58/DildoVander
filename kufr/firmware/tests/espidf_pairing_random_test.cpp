#include <cassert>

#include <array>
#include <algorithm>

#include "esp_wifi.h"
#include "../src/features/provisioning/EspIdfPairingRandom.h"

namespace {

bool connected = false;
bool disconnectDuringFill = false;
int fills = 0;

bool IsZero(const std::array<uint8_t, 32>& bytes) {
  return std::all_of(bytes.begin(), bytes.end(),
                     [](uint8_t value) { return value == 0; });
}

}  // namespace

int esp_wifi_sta_get_ap_info(wifi_ap_record_t*) {
  return connected ? ESP_OK : -1;
}

void esp_fill_random(void* output, size_t length) {
  ++fills;
  auto* bytes = static_cast<uint8_t*>(output);
  for (size_t index = 0; index < length; ++index) {
    bytes[index] = static_cast<uint8_t>(index + 1);
  }
  if (disconnectDuringFill) {
    connected = false;
  }
}

int main() {
  EspIdfPairingRandom random;
  std::array<uint8_t, 32> token{};
  token.fill(0xa5);
  assert(!random.Fill(nullptr, token.size()));
  assert(!random.Fill(token.data(), token.size() - 1));
  assert(!random.Fill(token.data(), token.size()));
  assert(IsZero(token));
  assert(fills == 0);

  connected = true;
  assert(random.Fill(token.data(), token.size()));
  assert(token.front() == 1 && token.back() == 32);
  assert(fills == 1);

  disconnectDuringFill = true;
  assert(!random.Fill(token.data(), token.size()));
  assert(IsZero(token));
  assert(fills == 2);
}

#pragma once

#include <stdint.h>

namespace KufrBoard {
struct Pins {
  uint8_t sda, scl, rgb, oe, bclk, ws, din, dout, mute;
};
constexpr Pins ProfileA{1, 14, 21, 47, 4, 5, 6, 7, 16};
constexpr Pins ProfileB{1, 14, 48, 2, 21, 41, 42, 47, 255};
constexpr bool Unique(const Pins& p) {
  const uint8_t pins[] = {p.sda, p.scl, p.rgb, p.oe, p.bclk,
                          p.ws, p.din, p.dout, p.mute};
  for (unsigned i = 0; i < 9; ++i) {
    if (pins[i] == 255) continue;
    for (unsigned j = i + 1; j < 9; ++j)
      if (pins[i] == pins[j]) return false;
    if (pins[i] == 0 || pins[i] == 3 || pins[i] == 19 || pins[i] == 20 ||
        (pins[i] >= 26 && pins[i] <= 40) || pins[i] == 43 ||
        pins[i] == 44 || pins[i] == 45 || pins[i] == 46) return false;
  }
  return true;
}
static_assert(Unique(ProfileA), "Profile A GPIO conflict");
static_assert(Unique(ProfileB), "Profile B GPIO conflict");
constexpr bool AvoidsCamera(const Pins& p) {
  const uint8_t pins[] = {p.sda, p.scl, p.rgb, p.oe, p.bclk, p.ws, p.din, p.dout};
  for (auto pin : pins) if (pin >= 4 && pin <= 18 && pin != 14) return false;
  return true;
}
static_assert(AvoidsCamera(ProfileB), "Profile B must preserve camera wiring");
constexpr uint8_t PcaAddress = 0x40;
constexpr uint8_t McpAddress = 0x20;
constexpr uint8_t LedCount = 36;
constexpr uint8_t LedsPerCompartment = 6;
constexpr uint8_t InitialBrightness = 64;
}  // namespace KufrBoard

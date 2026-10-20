#pragma once
#include <stddef.h>
#include <stdint.h>

namespace kufr { namespace offline {
// Firmware-bank framing: s16le initial predictor, u8 step index, reserved zero,
// then low nibble first. Sample count is explicit; no padding sample is played.
class ImaAdpcm final {
 public:
  bool Begin(const uint8_t* data, size_t bytes, uint32_t samples);
  size_t Read(uint8_t* pcm, size_t capacity);
 private:
  int predictor_ = 0, index_ = 0;
  const uint8_t* data_ = nullptr;
  uint32_t samples_ = 0, emitted_ = 0;
};
} }

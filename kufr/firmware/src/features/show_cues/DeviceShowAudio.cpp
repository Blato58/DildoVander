#include "DeviceShowAudio.h"
#include <cstring>

// C++14 requires storage when callers bind the constexpr member by reference.
constexpr size_t DeviceShowAudio::MaximumBytes;

namespace {
uint32_t U32(const uint8_t* bytes) {
  return bytes[0] | (uint32_t(bytes[1]) << 8) | (uint32_t(bytes[2]) << 16) | (uint32_t(bytes[3]) << 24);
}
uint16_t U16(const uint8_t* bytes) { return bytes[0] | (uint16_t(bytes[1]) << 8); }
}
DeviceShowAudio::~DeviceShowAudio() {
  if (storage_) {
    volatile uint8_t* bytes = storage_;
    for (size_t index = 0; index < used_; ++index) bytes[index] = 0;
    if (release_) release_(storage_);
  }
}
bool DeviceShowAudio::Append(const uint8_t* bytes, size_t length) {
  if (!storage_ || !bytes || ready_ || used_ > capacity_ || length > capacity_ - used_ ||
      used_ + length > MaximumBytes) return false;
  std::memcpy(storage_ + used_, bytes, length); used_ += length; return true;
}
bool DeviceShowAudio::Finish() {
  ready_ = storage_ && used_ >= 46 && used_ <= MaximumBytes && (used_ - 44) % 2 == 0 &&
      std::memcmp(storage_, "RIFF", 4) == 0 && U32(storage_ + 4) == used_ - 8 &&
      std::memcmp(storage_ + 8, "WAVEfmt ", 8) == 0 && U32(storage_ + 16) == 16 &&
      U16(storage_ + 20) == 1 && U16(storage_ + 22) == 1 && U32(storage_ + 24) == 32000 &&
      U32(storage_ + 28) == 64000 && U16(storage_ + 32) == 2 && U16(storage_ + 34) == 16 &&
      std::memcmp(storage_ + 36, "data", 4) == 0 && U32(storage_ + 40) == used_ - 44;
  return ready_;
}

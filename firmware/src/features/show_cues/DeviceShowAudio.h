#pragma once
#include <stddef.h>
#include <stdint.h>
#include <memory>

// Storage is explicitly supplied by the ESP adapter as verified PSRAM. Host
// tests use owned synthetic memory; no implicit half-megabyte heap allocation.
class DeviceShowAudio final {
 public:
  static constexpr size_t MaximumBytes = 512044;
  DeviceShowAudio(uint8_t* storage, size_t capacity, void (*release)(uint8_t*))
      : storage_(storage), capacity_(capacity), release_(release) {}
  ~DeviceShowAudio();
  bool Append(const uint8_t* bytes, size_t length);
  bool Finish();
  void Reset() { used_ = 0; ready_ = false; }
  const uint8_t* Pcm() const { return ready_ ? storage_ + 44 : nullptr; }
  size_t PcmBytes() const { return ready_ ? used_ - 44 : 0; }
  size_t Capacity() const { return storage_ ? capacity_ : 0; }
 private:
  uint8_t* storage_;
  size_t capacity_, used_ = 0;
  void (*release_)(uint8_t*);
  bool ready_ = false;
};

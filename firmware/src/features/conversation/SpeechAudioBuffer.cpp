#include "SpeechAudioBuffer.h"

#include <algorithm>
#include <cstring>

namespace {
uint32_t Read32(const uint8_t* bytes) {
  return static_cast<uint32_t>(bytes[0]) |
         (static_cast<uint32_t>(bytes[1]) << 8) |
         (static_cast<uint32_t>(bytes[2]) << 16) |
         (static_cast<uint32_t>(bytes[3]) << 24);
}
uint16_t Read16(const uint8_t* bytes) {
  return static_cast<uint16_t>(bytes[0]) |
         (static_cast<uint16_t>(bytes[1]) << 8);
}
bool IsDeviceWav(const uint8_t* bytes, size_t length) {
  // Both backend fake and normalized provider responses use this canonical
  // PCM header. Reject extra chunks instead of guessing a PCM data offset.
  return length >= 46 && length <= SpeechAudioBuffer::MaxWavBytes &&
         length % 2 == 0 && std::memcmp(bytes, "RIFF", 4) == 0 &&
         Read32(bytes + 4) == length - 8 &&
         std::memcmp(bytes + 8, "WAVEfmt ", 8) == 0 &&
         Read32(bytes + 16) == 16 && Read16(bytes + 20) == 1 &&
         Read16(bytes + 22) == 1 && Read32(bytes + 24) == 32000 &&
         Read32(bytes + 28) == 64000 && Read16(bytes + 32) == 2 &&
         Read16(bytes + 34) == 16 &&
         std::memcmp(bytes + 36, "data", 4) == 0 &&
         Read32(bytes + 40) == length - 44;
}
}  // namespace

SpeechAudioBuffer::SpeechAudioBuffer(uint8_t* storage, size_t capacity)
    : storage_(storage), capacity_(capacity) {}

SpeechAudioBuffer::~SpeechAudioBuffer() { Clear(); }

bool SpeechAudioBuffer::Begin(
    const std::string& operationId, const TurnCorrelation& expected,
    const SpeechOperationObservation& result,
    const ConversationRuntimeSnapshot& snapshot,
    const ConversationRuntimeState& runtime,
    uint64_t utcSeconds, uint64_t monotonicMs,
    size_t contentLength, const std::string& contentType,
    unsigned sampleRate, unsigned channels) {
  std::lock_guard<std::mutex> lock(mutex_);
  ClearLocked();
  const uint64_t ttl = SpeechRemainingMs(operationId, expected, utcSeconds, result);
  if (storage_ == nullptr || contentLength < 46 || contentLength > capacity_ ||
      contentLength > MaxWavBytes || contentLength % 2 != 0 ||
      contentType != "audio/wav" || sampleRate != 32000 || channels != 1 ||
      ttl == 0 || monotonicMs > UINT64_MAX - ttl || snapshot.version == 0 ||
      snapshot.generation != expected.generation ||
      snapshot.deviceRevision != expected.deviceRevision ||
      !runtime.Matches(snapshot)) {
    return false;
  }
  snapshot_ = snapshot;
  expectedLength_ = contentLength;
  expiresAtMs_ = monotonicMs + ttl;
  return true;
}

bool SpeechAudioBuffer::Append(const uint8_t* bytes, size_t length,
                              uint64_t nowMs,
                              const ConversationRuntimeState& runtime) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (bytes == nullptr || length == 0 || ready_ ||
      !IsCurrentLocked(nowMs, runtime) || length > expectedLength_ - used_) {
    ClearLocked();
    return false;
  }
  std::memcpy(storage_ + used_, bytes, length);
  used_ += length;
  return true;
}

bool SpeechAudioBuffer::Finish(uint64_t nowMs,
                              const ConversationRuntimeState& runtime) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (ready_ || !IsCurrentLocked(nowMs, runtime) || used_ != expectedLength_ ||
      !IsDeviceWav(storage_, used_)) {
    ClearLocked();
    return false;
  }
  ready_ = true;
  return true;
}

bool SpeechAudioBuffer::HasReadyPcm(uint64_t nowMs,
                                  const ConversationRuntimeState& runtime) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (!IsCurrentLocked(nowMs, runtime)) {
    ClearLocked();
    return false;
  }
  return ready_;
}

size_t SpeechAudioBuffer::ReadPcm(size_t offset, uint8_t* output,
                                 size_t capacity, uint64_t nowMs,
                                 const ConversationRuntimeState& runtime) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (!IsCurrentLocked(nowMs, runtime)) {
    ClearLocked();
    return 0;
  }
  if (!ready_ || output == nullptr || capacity < 2 || offset % 2 != 0 ||
      offset >= used_ - 44) {
    return 0;
  }
  const size_t count = std::min(capacity - capacity % 2, used_ - 44 - offset);
  std::memcpy(output, storage_ + 44 + offset, count);
  return count;
}

bool SpeechAudioBuffer::IsCurrentLocked(
    uint64_t nowMs, const ConversationRuntimeState& runtime) const {
  return expectedLength_ != 0 && nowMs < expiresAtMs_ && runtime.Matches(snapshot_);
}

void SpeechAudioBuffer::Clear() {
  std::lock_guard<std::mutex> lock(mutex_);
  ClearLocked();
}

void SpeechAudioBuffer::ClearLocked() {
  volatile uint8_t* bytes = storage_;
  for (size_t index = 0; index < used_; ++index) {
    bytes[index] = 0;
  }
  used_ = 0;
  expectedLength_ = 0;
  expiresAtMs_ = 0;
  snapshot_ = {};
  ready_ = false;
}

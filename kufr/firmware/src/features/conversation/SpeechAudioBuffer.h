#pragma once

#include <cstddef>
#include <cstdint>
#include <mutex>

#include "ConversationRuntimeState.h"
#include "SpeechOperationCodec.h"

// Caller supplies one verified RAM/PSRAM allocation. No implicit 2 MB heap
// allocation, no playback or actuator command. Read only copies bounded DMA
// chunks after the whole WAV passes validation and the snapshot is current.
class SpeechAudioBuffer final {
 public:
  static constexpr size_t MaxWavBytes = 44 + 32000 * 2 * 30;
  SpeechAudioBuffer(uint8_t* storage, size_t capacity);
  ~SpeechAudioBuffer();
  SpeechAudioBuffer(const SpeechAudioBuffer&) = delete;
  SpeechAudioBuffer& operator=(const SpeechAudioBuffer&) = delete;

  bool Begin(const std::string& operationId, const TurnCorrelation& expected,
             const SpeechOperationObservation& result,
             const ConversationRuntimeSnapshot& snapshot,
             const ConversationRuntimeState& runtime,
             uint64_t utcSeconds, uint64_t monotonicMs,
             size_t contentLength, const std::string& contentType,
             unsigned sampleRate, unsigned channels);
  bool Append(const uint8_t* bytes, size_t length, uint64_t nowMs,
              const ConversationRuntimeState& runtime);
  bool Finish(uint64_t nowMs, const ConversationRuntimeState& runtime);
  bool HasReadyPcm(uint64_t nowMs, const ConversationRuntimeState& runtime);
  size_t ReadPcm(size_t offset, uint8_t* output, size_t capacity,
                 uint64_t nowMs, const ConversationRuntimeState& runtime);
  void Clear();

 private:
  bool IsCurrentLocked(uint64_t nowMs,
                       const ConversationRuntimeState& runtime) const;
  void ClearLocked();
  uint8_t* storage_;
  size_t capacity_;
  size_t expectedLength_ = 0;
  size_t used_ = 0;
  uint64_t expiresAtMs_ = 0;
  ConversationRuntimeSnapshot snapshot_;
  bool ready_ = false;
  std::mutex mutex_;
};

#pragma once
#include <array>
#include <cstddef>
#include <cstdint>
#include <string>
#include "DancePlayerPort.h"

namespace kufr { namespace dance {
constexpr size_t MaximumDanceTracks = 12;
constexpr size_t MaximumDanceManifestBytes = 2048;
constexpr size_t DanceBlockBytes = 960;
constexpr uint32_t DanceSampleRate = 24000;
struct DanceTrack {
  std::string filename;
  uint16_t bpm = 0;
  uint32_t offsetMs = 0;
  uint8_t red = 0, green = 0, blue = 0, vibrationEveryBeats = 0;
  uint32_t dataBytes = 0;
  uint32_t durationMs = 0;
};
struct DanceManifest {
  std::array<DanceTrack, MaximumDanceTracks> tracks;
  size_t count = 0;
};
bool IsDanceFilename(const std::string& name);
bool ParseDanceManifest(const std::string& text, DanceManifest& output, std::string& error);
bool ParseDanceWav(const uint8_t* header, size_t size, uint64_t fileBytes,
                   DanceTrack& track, std::string& error);
size_t SelectDanceTrack(size_t count, size_t previous, uint32_t random);
void DescribeDanceTrack(const DanceTrack& track, DanceStatus& status);

// I2S has no consumed-frame counter in VoiceAudioPort. Estimate audible time
// from the first submission, capped by submitted PCM frames (DMA approximation).
class DanceAudioTimeline final {
 public:
  void Start(uint64_t nowMs) { startedAt_ = nowMs; submittedFrames_ = 0; }
  void Submitted(size_t bytes) { submittedFrames_ += bytes / 2; }
  uint32_t Position(uint64_t nowMs) const;
  bool Underrun(uint64_t nowMs) const;
 private:
  uint64_t startedAt_ = 0;
  uint64_t submittedFrames_ = 0;
};
} }

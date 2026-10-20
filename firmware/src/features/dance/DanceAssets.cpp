#include "DanceAssets.h"
#include <algorithm>
#include <cstring>
#include <vector>

namespace kufr { namespace dance {
namespace {
bool Number(const std::string& text, uint32_t minimum, uint32_t maximum, uint32_t& value) {
  if (text.empty() || text.size() > 5) return false;
  value = 0;
  for (char ch : text) {
    if (ch < '0' || ch > '9') return false;
    value = value * 10 + static_cast<unsigned>(ch - '0');
    if (value > maximum) return false;
  }
  return value >= minimum;
}
uint32_t Le32(const uint8_t* bytes) {
  return static_cast<uint32_t>(bytes[0]) | static_cast<uint32_t>(bytes[1]) << 8 |
      static_cast<uint32_t>(bytes[2]) << 16 | static_cast<uint32_t>(bytes[3]) << 24;
}
uint16_t Le16(const uint8_t* bytes) { return bytes[0] | static_cast<uint16_t>(bytes[1]) << 8; }
bool Fail(std::string& error, const char* reason) { error = reason; return false; }
}
bool IsDanceFilename(const std::string& name) {
  if (name.size() < 5 || name.size() > 64 || name.substr(name.size() - 4) != ".wav") return false;
  const char first = name.front();
  if (!((first >= 'a' && first <= 'z') || (first >= 'A' && first <= 'Z') ||
        (first >= '0' && first <= '9'))) return false;
  for (size_t i = 0; i < name.size() - 4; ++i) {
    const char ch = name[i];
    if (!((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') ||
          (ch >= '0' && ch <= '9') || ch == '-' || ch == '_')) return false;
  }
  return true;
}
bool ParseDanceManifest(const std::string& text, DanceManifest& output, std::string& error) {
  output = {}; error.clear();
  if (text.empty() || text.size() > MaximumDanceManifestBytes)
    return Fail(error, "dance-config-size");
  for (unsigned char ch : text)
    if (ch != '\n' && ch != '\r' && (ch < 32 || ch > 126)) return Fail(error, "dance-config-format");
  DanceManifest parsed;
  size_t start = 0, lineNumber = 0;
  while (start < text.size()) {
    const size_t end = text.find('\n', start);
    std::string line = text.substr(start, end == std::string::npos ? text.size() - start : end - start);
    if (!line.empty() && line.back() == '\r') line.pop_back();
    start = end == std::string::npos ? text.size() : end + 1;
    if (lineNumber++ == 0) {
      if (line != "KUFR_DANCE_V1" || end == std::string::npos) return Fail(error, "dance-config-format");
      continue;
    }
    if (line.empty() || parsed.count == MaximumDanceTracks) return Fail(error, "dance-config-format");
    std::vector<std::string> fields;
    size_t fieldStart = 0;
    for (;;) {
      const size_t comma = line.find(',', fieldStart);
      fields.push_back(line.substr(fieldStart, comma == std::string::npos ? line.size() - fieldStart : comma - fieldStart));
      if (comma == std::string::npos) break;
      fieldStart = comma + 1;
      if (fields.size() >= 7) return Fail(error, "dance-config-format");
    }
    if (fields.size() != 7 || !IsDanceFilename(fields[0])) return Fail(error, "dance-config-format");
    // FAT names are case insensitive; reject aliases as well as exact duplicates.
    std::string folded = fields[0];
    std::transform(folded.begin(), folded.end(), folded.begin(), [](char ch) { return ch >= 'A' && ch <= 'Z' ? ch + 32 : ch; });
    for (size_t i = 0; i < parsed.count; ++i) {
      std::string other = parsed.tracks[i].filename;
      std::transform(other.begin(), other.end(), other.begin(), [](char ch) { return ch >= 'A' && ch <= 'Z' ? ch + 32 : ch; });
      if (other == folded) return Fail(error, "dance-config-duplicate");
    }
    uint32_t values[6]{};
    const uint32_t minimum[6] = {40, 0, 0, 0, 0, 0};
    const uint32_t maximum[6] = {240, 10000, 255, 255, 255, 16};
    for (size_t i = 0; i < 6; ++i)
      if (!Number(fields[i + 1], minimum[i], maximum[i], values[i])) return Fail(error, "dance-config-range");
    DanceTrack& track = parsed.tracks[parsed.count++];
    track.filename = fields[0]; track.bpm = static_cast<uint16_t>(values[0]); track.offsetMs = values[1];
    track.red = static_cast<uint8_t>(values[2]); track.green = static_cast<uint8_t>(values[3]);
    track.blue = static_cast<uint8_t>(values[4]); track.vibrationEveryBeats = static_cast<uint8_t>(values[5]);
  }
  if (!parsed.count) return Fail(error, "dance-config-empty");
  output = std::move(parsed); return true;
}
bool ParseDanceWav(const uint8_t* header, size_t size, uint64_t fileBytes,
                   DanceTrack& track, std::string& error) {
  error.clear();
  if (!header || size != 44 || std::memcmp(header, "RIFF", 4) ||
      std::memcmp(header + 8, "WAVEfmt ", 8) || std::memcmp(header + 36, "data", 4) ||
      Le32(header + 16) != 16 || Le16(header + 20) != 1 || Le16(header + 22) != 1 ||
      Le32(header + 24) != DanceSampleRate || Le32(header + 28) != DanceSampleRate * 2 ||
      Le16(header + 32) != 2 || Le16(header + 34) != 16) return Fail(error, "dance-wav-format");
  const uint32_t bytes = Le32(header + 40);
  if (bytes < DanceSampleRate * 2 || bytes > DanceSampleRate * 2 * 600 || bytes % 2 ||
      fileBytes != static_cast<uint64_t>(bytes) + 44 || Le32(header + 4) != bytes + 36)
    return Fail(error, "dance-wav-size");
  const uint32_t duration = bytes / 48;
  if (track.offsetMs >= duration) return Fail(error, "dance-config-offset");
  track.dataBytes = bytes; track.durationMs = duration; return true;
}
size_t SelectDanceTrack(size_t count, size_t previous, uint32_t random) {
  if (count < 2) return 0;
  if (previous >= count) return random % count;
  const size_t selected = random % (count - 1);
  return selected >= previous ? selected + 1 : selected;
}
void DescribeDanceTrack(const DanceTrack& track, DanceStatus& status) {
  status.track = track.filename; status.durationMs = track.durationMs; status.bpm = track.bpm;
  status.beatOffsetMs = track.offsetMs; status.red = track.red; status.green = track.green;
  status.blue = track.blue; status.vibrationEveryBeats = track.vibrationEveryBeats;
}
uint32_t DanceAudioTimeline::Position(uint64_t nowMs) const {
  const uint64_t elapsed = nowMs >= startedAt_ ? nowMs - startedAt_ : 0;
  return static_cast<uint32_t>(std::min(elapsed, submittedFrames_ * 1000 / DanceSampleRate));
}
bool DanceAudioTimeline::Underrun(uint64_t nowMs) const {
  // Submitted PCM includes the DMA lead (about 80 ms). More than one 20 ms
  // block beyond ALL submitted frames signals an underrun, not musical time.
  return nowMs < startedAt_ || nowMs - startedAt_ > submittedFrames_ * 1000 / DanceSampleRate + 20;
}
} }

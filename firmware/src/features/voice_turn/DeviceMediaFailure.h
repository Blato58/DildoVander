#pragma once
#include <cstring>

// Detailed diagnostics are fixed device-owned codes. Existing LAN clients must
// still receive only their negotiated voiceStatus.error vocabulary.
inline const char* PublicDeviceMediaError(const char* diagnostic) {
  return diagnostic && (std::strncmp(diagnostic, "audio_", 6) == 0 ||
      std::strcmp(diagnostic, "media_capacity_unavailable") == 0) ? "audio_failed" : "ai_unavailable";
}

#pragma once

#include <algorithm>
#include <string>

// A Lab ticket is an opaque RAM-only credential, not a provider instruction or
// physical authority. Only its SHA256 identifier may appear in local status.
inline bool ValidVoiceTestContext(const std::string& value) {
  if (value.size() != 43) return false;
  for (char ch : value)
    if (!((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') ||
          (ch >= '0' && ch <= '9') || ch == '-' || ch == '_')) return false;
  return true;
}

inline bool ValidVoiceTestContextId(const std::string& value) {
  if (value.size() != 64) return false;
  for (char ch : value)
    if (!((ch >= 'a' && ch <= 'f') || (ch >= '0' && ch <= '9'))) return false;
  return true;
}

inline bool MatchesActiveVoiceTestContext(const std::string& activeId,
    const std::string& requestedId, bool currentVoice, bool currentPhysical) {
  return ValidVoiceTestContextId(requestedId) && activeId == requestedId &&
      (currentVoice || currentPhysical);
}

inline void ClearVoiceTestContext(std::string& value) {
  std::fill(value.begin(), value.end(), '\0');
  value.clear();
}

struct VoiceTestContextLease final {
  std::string value;
  ~VoiceTestContextLease() { ClearVoiceTestContext(value); }
};

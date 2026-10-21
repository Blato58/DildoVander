#pragma once

#include <stdint.h>

#include <string>

// Wire epochs use canonical decimal strings so JavaScript cannot round them.
inline bool ParseOperationEpoch(const std::string& value, uint64_t& epoch) {
  if (value.empty() || value.size() > 20 ||
      (value.size() > 1 && value[0] == '0')) return false;
  uint64_t parsed = 0;
  for (char character : value) {
    if (character < '0' || character > '9') return false;
    const uint64_t digit = static_cast<uint64_t>(character - '0');
    if (parsed > (UINT64_MAX - digit) / 10) return false;
    parsed = parsed * 10 + digit;
  }
  epoch = parsed;
  return true;
}

#include "ReconnectPolicy.h"

#include <algorithm>
#include <cstddef>

namespace {

constexpr uint32_t BaseDelaysMs[] = {1000, 2000, 4000,
                                     8000, 16000, 30000};

}  // namespace

uint32_t ReconnectPolicy::NextDelayMs(uint16_t jitterPermille) {
  const size_t index = std::min(static_cast<size_t>(attempt_),
                                sizeof(BaseDelaysMs) / sizeof(BaseDelaysMs[0]) - 1);
  if (attempt_ < 5) {
    ++attempt_;
  }
  const uint16_t boundedJitter = std::min<uint16_t>(jitterPermille, 400);
  const uint32_t scaled =
      BaseDelaysMs[index] * (800u + boundedJitter) / 1000u;
  return std::min<uint32_t>(scaled, 30000);
}

void ReconnectPolicy::Reset() { attempt_ = 0; }

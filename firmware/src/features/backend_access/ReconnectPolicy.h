#pragma once

#include <stdint.h>

class ReconnectPolicy final {
 public:
  uint32_t NextDelayMs(uint16_t jitterPermille);
  void Reset();

 private:
  uint8_t attempt_ = 0;
};

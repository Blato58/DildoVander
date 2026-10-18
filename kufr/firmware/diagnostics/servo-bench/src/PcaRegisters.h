#pragma once
#include "ServoBench.h"

namespace ServoBench {
class RegisterBus {
 public:
  virtual ~RegisterBus() = default;
  virtual bool Write(uint8_t reg, const uint8_t* values, std::size_t size) = 0;
  virtual bool Read(uint8_t reg, uint8_t& value) = 0;
  virtual void WaitOscillator() = 0;
};

inline bool DisableAllPca(RegisterBus& bus) {
  // ALL_LED_OFF_H fans out FULL_OFF to every LEDn_OFF_H. This single-byte
  // write works even before MODE1 auto-increment is configured. FULL_OFF
  // takes precedence over stale FULL_ON bits (NXP section 7.3.3).
  const uint8_t fullOff = 0x10;
  return bus.Write(0xfd, &fullOff, 1);
}

inline bool ClearChannels(RegisterBus& bus) {
  uint8_t off[64]{};
  for (unsigned channel = 0; channel < 16; ++channel) off[channel * 4 + 3] = 0x10;
  return bus.Write(0x06, off, sizeof(off));
}

inline bool PreparePca(RegisterBus& bus) {
  const uint8_t sleep = 0x30, mode2 = 0x04, prescale = Prescale, wake = 0x20;
  // ALL_LED_* writes would overwrite every channel, not set independent flags.
  if (!DisableAllPca(bus) || !bus.Write(0x00, &sleep, 1) || !bus.Write(0x01, &mode2, 1) ||
      !bus.Write(0xfe, &prescale, 1) || !ClearChannels(bus) ||
      !bus.Write(0x00, &wake, 1)) return false;
  bus.WaitOscillator();
  uint8_t actualMode1 = 0, actualMode2 = 0, actualPrescale = 0;
  return bus.Read(0x00, actualMode1) && bus.Read(0x01, actualMode2) &&
      bus.Read(0xfe, actualPrescale) && actualMode1 == wake &&
      actualMode2 == mode2 && actualPrescale == prescale;
}

inline bool SetPulse(RegisterBus& bus, unsigned channel, unsigned ticks) {
  if (channel > 5 || ticks < PulseTicks(1400) || ticks > PulseTicks(1600) ||
      !DisableAllPca(bus) || !ClearChannels(bus)) return false;
  const uint8_t values[] = {0, 0, static_cast<uint8_t>(ticks & 0xff),
                          static_cast<uint8_t>((ticks >> 8) & 0x0f)};
  return bus.Write(static_cast<uint8_t>(0x06 + channel * 4), values, sizeof(values));
}
}  // namespace ServoBench

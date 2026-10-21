#pragma once
#include "ServoBench.h"

namespace ServoBench {
class RegisterBus {
 public:
  virtual ~RegisterBus() = default;
  virtual bool Write(uint8_t reg, const uint8_t* values, std::size_t size) = 0;
  virtual bool Read(uint8_t reg, uint8_t& value) = 0;
  virtual bool ReadBytes(uint8_t reg, uint8_t* values, std::size_t size) {
    for (std::size_t i = 0; i < size; ++i) {
      if (!Read(static_cast<uint8_t>(reg + i), values[i])) return false;
    }
    return true;
  }
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
  if (channel > 5 || ticks == 0 || ticks > PulseTicks(MaximumCommandPulseUs) ||
      !DisableAllPca(bus) || !ClearChannels(bus)) return false;
  const uint8_t values[] = {0, 0, static_cast<uint8_t>(ticks & 0xff),
                          static_cast<uint8_t>((ticks >> 8) & 0x0f)};
  return bus.Write(static_cast<uint8_t>(0x06 + channel * 4), values, sizeof(values));
}

inline bool EncodeFrame(unsigned mask, const unsigned ticks[6], uint8_t values[24]) {
  if (mask == 0 || mask > 0x3f) return false;
  for (unsigned channel = 0; channel < 6; ++channel) {
    const bool selected = (mask & (1U << channel)) != 0;
    if ((selected && (ticks[channel] < PulseTicks(CalibrationMinimumUs) ||
                      ticks[channel] > PulseTicks(CalibrationMaximumUs))) ||
        (!selected && ticks[channel] != 0)) return false;
    values[channel * 4] = 0;
    values[channel * 4 + 1] = 0;
    values[channel * 4 + 2] = static_cast<uint8_t>(ticks[channel] & 0xff);
    values[channel * 4 + 3] = selected ? static_cast<uint8_t>((ticks[channel] >> 8) & 0x0f) : 0x10;
  }
  return true;
}

struct PwmSnapshot {
  uint8_t mode1 = 0;
  uint8_t mode2 = 0;
  uint8_t prescale = 0;
  uint8_t onLow = 0;
  uint8_t onHigh = 0;
  uint8_t offLow = 0;
  uint8_t offHigh = 0;

  unsigned OnTicks() const { return onLow | ((onHigh & 0x0f) << 8); }
  unsigned OffTicks() const { return offLow | ((offHigh & 0x0f) << 8); }
  bool FullOn() const { return (onHigh & 0x10) != 0; }
  bool FullOff() const { return (offHigh & 0x10) != 0; }
  bool Matches(unsigned ticks) const {
    // RESTART is self-clearing; all remaining mode bits must match the setup.
    return (mode1 & 0x7f) == 0x20 && mode2 == 0x04 && prescale == Prescale &&
        onHigh == 0 && onLow == 0 && offHigh == ((ticks >> 8) & 0x0f) &&
        offLow == (ticks & 0xff);
  }
};

inline bool ReadPwmSnapshot(RegisterBus& bus, unsigned channel, PwmSnapshot& snapshot) {
  if (channel > 5) return false;
  const uint8_t reg = static_cast<uint8_t>(0x06 + channel * 4);
  uint8_t values[4]{};
  if (!bus.Read(0x00, snapshot.mode1) || !bus.Read(0x01, snapshot.mode2) ||
      !bus.Read(0xfe, snapshot.prescale) || !bus.ReadBytes(reg, values, sizeof(values))) return false;
  snapshot.onLow = values[0];
  snapshot.onHigh = values[1];
  snapshot.offLow = values[2];
  snapshot.offHigh = values[3];
  return true;
}

enum class PulseReadbackResult { WriteFailed, ReadFailed, Mismatch, Match };

inline PulseReadbackResult SetVerifiedPulse(
    RegisterBus& bus, unsigned channel, unsigned ticks, PwmSnapshot& snapshot) {
  if (!SetPulse(bus, channel, ticks)) return PulseReadbackResult::WriteFailed;
  if (!ReadPwmSnapshot(bus, channel, snapshot)) {
    DisableAllPca(bus); // Stop before logging; controller also retries when latching its fault.
    return PulseReadbackResult::ReadFailed;
  }
  if (!snapshot.Matches(ticks)) {
    DisableAllPca(bus);
    return PulseReadbackResult::Mismatch;
  }
  return PulseReadbackResult::Match;
}

inline PulseReadbackResult SetVerifiedFrame(RegisterBus& bus, unsigned mask, const unsigned ticks[6]) {
  uint8_t expected[24]{}, actual[24]{};
  // OCH=0 applies all six channels together on the single write's STOP.
  // Channels6..15 remain as prepared FULL_OFF; inactive0..5 are explicitly off.
  if (!EncodeFrame(mask, ticks, expected) || !bus.Write(0x06, expected, sizeof(expected))) {
    return PulseReadbackResult::WriteFailed;
  }
  uint8_t mode1 = 0, mode2 = 0, prescale = 0;
  if (!bus.Read(0x00, mode1) || !bus.Read(0x01, mode2) || !bus.Read(0xfe, prescale) ||
      !bus.ReadBytes(0x06, actual, sizeof(actual))) return PulseReadbackResult::ReadFailed;
  return (mode1 & 0x7f) == 0x20 && mode2 == 0x04 && prescale == Prescale &&
      std::memcmp(expected, actual, sizeof(actual)) == 0 ?
      PulseReadbackResult::Match : PulseReadbackResult::Mismatch;
}
}  // namespace ServoBench

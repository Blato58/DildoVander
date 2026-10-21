#pragma once

#include <cstddef>
#include <cstdio>
#include <cstdint>
#include <cstring>

namespace ServoBench {
constexpr uint32_t OscillatorHz = 25000000;
constexpr unsigned Prescale = 121;
constexpr unsigned CalibrationMinimumUs = 600;
constexpr unsigned CalibrationMaximumUs = 2300;
constexpr unsigned MaximumCommandPulseUs = 9999;
constexpr uint64_t PulseDurationUs = 500000;
constexpr uint64_t ArmDurationUs = 10000000;
constexpr uint64_t DanceWatchdogUs = 250000;
constexpr uint64_t DanceMaximumUs = 600000000;
constexpr unsigned DanceSlewUsPerSecond = 4000;

static_assert(CalibrationMinimumUs >= 5 && CalibrationMinimumUs <= CalibrationMaximumUs,
    "Servo calibration pulse-width limits must be positive and ordered");
static_assert(CalibrationMaximumUs <= MaximumCommandPulseUs,
    "Servo calibration pulse-width limits must fit the four-digit command grammar");
inline unsigned PulseTicks(unsigned microseconds) {
  return static_cast<unsigned>((static_cast<uint64_t>(microseconds) * OscillatorHz +
      (Prescale + 1) * 500000ULL) / ((Prescale + 1) * 1000000ULL));
}

class Outputs {
 public:
  virtual ~Outputs() = default;
  virtual bool Disable() = 0;
  virtual bool Prepare() = 0;
  virtual bool Pulse(unsigned channel, unsigned ticks) = 0;
  virtual bool Frame(unsigned mask, const unsigned ticks[6]) = 0;
  virtual uint64_t NowUs(uint64_t fallback) { return fallback; }
  virtual void Report(const char* message) = 0;
};

// Single owner, no queued movements. Times are monotonic microseconds.
class Controller {
 public:
  explicit Controller(Outputs& outputs, uint32_t bootNonce = 1)
      : outputs_(outputs), bootNonce_(bootNonce) { Stop("PWM FULL_OFF; disarmed"); }

  bool Faulted() const { return faulted_; }

  void Tick(uint64_t now) {
    if (dancing_ && (now >= deadline_ || now >= danceLimit_)) {
      Stop("DANCE TIMEOUT PWM FULL_OFF");
    } else if (active_ && now >= deadline_) {
      Stop("DONE PWM FULL_OFF; arm again for another pulse");
    } else if (armed_ && now >= deadline_) {
      Stop("ARM expired");
    }
  }

  void Fault() {
    faulted_ = true;
    Stop("FAULT PWM FULL_OFF; restart required");
  }

  void Feed(char byte, uint64_t now) {
    Tick(now);
    // Emergency stop is recognized even inside an incomplete/overflowed line.
    if (byte == '!') {
      Stop("STOP PWM FULL_OFF");
      length_ = 0;
      dropping_ = true;
      return;
    }
    if (byte == '\r' || byte == '\n') {
      if (!dropping_ && length_ != 0) {
        line_[length_] = '\0';
        Command(now);
      }
      length_ = 0;
      dropping_ = false;
      return;
    }
    if (dropping_) return;
    if (byte < 32 || byte > 126 || length_ == sizeof(line_) - 1) {
      Stop("REJECT invalid/overlong line; PWM FULL_OFF");
      length_ = 0;
      dropping_ = true;
      return;
    }
    line_[length_++] = byte;
  }

 private:
  bool Stop(const char* message) {
    active_ = false;
    armed_ = false;
    dancing_ = false;
    session_ = 0;
    armToken_ = 0;
    if (!outputs_.Disable()) {
      faulted_ = true;
      outputs_.Report("FAULT PWM_OFF_FAILED; PWM may still be active; cut servo power; restart required");
      return false;
    }
    outputs_.Report(message);
    return true;
  }

  void Command(uint64_t now) {
    if (std::strcmp(line_, "stop") == 0) {
      Stop("STOP PWM FULL_OFF");
      return;
    }
    if (dancing_) {
      if (std::strncmp(line_, "target ", 7) == 0) Target(now);
      else Stop("REJECT dance active; PWM FULL_OFF");
      return;
    }
    if (std::strcmp(line_, "help") == 0) {
      char help[160];
      std::snprintf(help, sizeof(help),
          "arm | pulse <channel 0-5> <us 1400-1600> | cal <channel 0-5> <us %04u-%04u> "
          "<ms 0500-3000> | stop | ! | help",
          CalibrationMinimumUs, CalibrationMaximumUs);
      outputs_.Report(help);
      return;
    }
    if (faulted_) {
      Stop("FAULT restart required");
      return;
    }
    if (active_) {
      Stop("REJECT busy; PWM FULL_OFF");
      return;
    }
    if (std::strcmp(line_, "arm") == 0) {
      if (!Stop("Preparing PCA with all PWM off")) return;
      if (!outputs_.Prepare()) {
        Fault();
        return;
      }
      if (armCounter_ == 0xffffffffU) {
        Fault();
        return;
      }
      armToken_ = ++armCounter_;
      armed_ = true;
      deadline_ = now + ArmDurationUs;
      char message[112];
      std::snprintf(message, sizeof(message),
          "ARMED for one pulse, 10 seconds; servo must be unloaded; dance_token=%08lx",
          static_cast<unsigned long>(armToken_));
      outputs_.Report(message);
      return;
    }
    if (std::strncmp(line_, "dance ", 6) == 0) {
      StartDance(now);
      return;
    }
    // Calibration has a separate fixed grammar; legacy pulse keeps its narrow range.
    if (std::strlen(line_) == 15 && std::memcmp(line_, "cal ", 4) == 0 &&
        line_[4] >= '0' && line_[4] <= '5' && line_[5] == ' ' && line_[10] == ' ') {
      unsigned microseconds = 0;
      unsigned milliseconds = 0;
      if (!ParseFourDigits(line_ + 6, microseconds) ||
          !ParseFourDigits(line_ + 11, milliseconds)) {
        Stop("REJECT cal syntax");
        return;
      }
      if (!armed_ || microseconds < CalibrationMinimumUs || microseconds > CalibrationMaximumUs ||
          milliseconds < 500 || milliseconds > 3000) {
        Stop("REJECT arm first; cal pulse width outside configured limits or duration outside 0500-3000 ms");
        return;
      }
      armed_ = false;
      if (!outputs_.Pulse(static_cast<unsigned>(line_[4] - '0'), PulseTicks(microseconds))) {
        Fault();
        return;
      }
      active_ = true;
      deadline_ = now + static_cast<uint64_t>(milliseconds) * 1000;
      char message[96];
      std::snprintf(message, sizeof(message),
          "PULSE nominal %u ms; calibration; timeout/Stop writes PCA FULL_OFF", milliseconds);
      outputs_.Report(message);
      return;
    }
    // Fixed grammar avoids signs, integer overflow, trailing data and channel 6+.
    if (std::strlen(line_) == 12 && std::memcmp(line_, "pulse ", 6) == 0 &&
        line_[6] >= '0' && line_[6] <= '5' && line_[7] == ' ') {
      unsigned microseconds = 0;
      for (std::size_t i = 8; i < 12; ++i) {
        if (line_[i] < '0' || line_[i] > '9') {
          Stop("REJECT pulse syntax");
          return;
        }
        microseconds = microseconds * 10 + static_cast<unsigned>(line_[i] - '0');
      }
      if (!armed_ || microseconds < 1400 || microseconds > 1600) {
        Stop("REJECT arm first; permitted pulse is 1400-1600 us");
        return;
      }
      armed_ = false;
      if (!outputs_.Pulse(static_cast<unsigned>(line_[6] - '0'), PulseTicks(microseconds))) {
        Fault();
        return;
      }
      active_ = true;
      deadline_ = now + PulseDurationUs;
      outputs_.Report("PULSE nominal 500 ms; timeout/Stop writes PCA FULL_OFF");
      return;
    }
    Stop("REJECT unknown command; type help");
  }

  static bool ParseFourDigits(const char* text, unsigned& value) {
    value = 0;
    for (std::size_t i = 0; i < 4; ++i) {
      if (text[i] < '0' || text[i] > '9') return false;
      value = value * 10 + static_cast<unsigned>(text[i] - '0');
    }
    return true;
  }

  static bool ParseHex(const char* text, uint32_t& value) {
    value = 0;
    for (std::size_t i = 0; i < 8; ++i) {
      const char digit = text[i];
      if (!((digit >= '0' && digit <= '9') || (digit >= 'a' && digit <= 'f'))) return false;
      value = (value << 4) | static_cast<uint32_t>(digit <= '9' ? digit - '0' : digit - 'a' + 10);
    }
    return true;
  }

  void StartDance(uint64_t now) {
    uint32_t boot = 0, session = 0;
    unsigned mask = 0;
    if (std::strlen(line_) != 26 || !ParseMask(line_ + 6, mask) ||
        line_[8] != ' ' || line_[17] != ' ' || !ParseHex(line_ + 9, boot) ||
        !ParseHex(line_ + 18, session) || !armed_ || boot == 0 || boot != bootNonce_ ||
        session == 0 || session != armToken_) {
      Stop("REJECT dance syntax/arm/boot/session; PWM FULL_OFF");
      return;
    }
    // Each successful arm issues a unique token for this boot. Consuming it
    // prevents buffered commands from reviving an earlier session.
    session_ = session;
    armToken_ = 0;
    danceMask_ = mask;
    armed_ = false;
    dancing_ = true;
    lastSequence_ = 0;
    for (unsigned channel = 0; channel < 6; ++channel) lastTargetUs_[channel] = 1450;
    lastTargetTime_ = now;
    deadline_ = now + DanceWatchdogUs;
    danceLimit_ = now + DanceMaximumUs;
    char message[96];
    std::snprintf(message, sizeof(message), "DANCE READY token=%08lx mask=%02x watchdog_ms=250",
        static_cast<unsigned long>(session_), danceMask_);
    outputs_.Report(message);
  }

  void Target(uint64_t now) {
    uint32_t session = 0;
    unsigned sequence = 0;
    if (std::strlen(line_) != 46 || line_[15] != ' ' || line_[21] != ' ' ||
        !ParseHex(line_ + 7, session) || session != session_) {
      Stop("REJECT target syntax/session; PWM FULL_OFF");
      return;
    }
    for (std::size_t i = 16; i < 21; ++i) {
      if (line_[i] < '0' || line_[i] > '9') {
        Stop("REJECT target sequence; PWM FULL_OFF");
        return;
      }
      sequence = sequence * 10 + static_cast<unsigned>(line_[i] - '0');
    }
    if (sequence <= lastSequence_ || now < lastTargetTime_) {
      Stop("REJECT target sequence/range; PWM FULL_OFF");
      return;
    }
    const uint64_t elapsed = now - lastTargetTime_;
    const unsigned delta = static_cast<unsigned>(
        (elapsed < DanceWatchdogUs ? elapsed : DanceWatchdogUs) * DanceSlewUsPerSecond / 1000000);
    unsigned microseconds[6]{}, ticks[6]{};
    char applied[25]{};
    for (unsigned channel = 0; channel < 6; ++channel) {
      const bool selected = (danceMask_ & (1U << channel)) != 0;
      if (!ParseFourDigits(line_ + 22 + channel * 4, microseconds[channel]) ||
          (selected && (microseconds[channel] < CalibrationMinimumUs ||
                        microseconds[channel] > CalibrationMaximumUs)) ||
          (!selected && microseconds[channel] != 0)) {
        Stop("REJECT target range/mask; PWM FULL_OFF");
        return;
      }
      if (selected) {
        const unsigned minimum = lastTargetUs_[channel] > delta ? lastTargetUs_[channel] - delta : 0;
        const unsigned maximum = lastTargetUs_[channel] + delta;
        if (microseconds[channel] < minimum) microseconds[channel] = minimum;
        if (microseconds[channel] > maximum) microseconds[channel] = maximum;
        ticks[channel] = PulseTicks(microseconds[channel]);
      }
      std::snprintf(applied + channel * 4, 5, "%04u", microseconds[channel]);
    }
    if (!outputs_.Frame(danceMask_, ticks)) {
      Fault();
      return;
    }
    const uint64_t completed = outputs_.NowUs(now);
    // A synchronous write/read may have consumed the remaining lease. Never
    // acknowledge or renew a target that completed at/after either deadline.
    if (completed < now || completed >= deadline_ || completed >= danceLimit_) {
      Stop("DANCE TIMEOUT PWM FULL_OFF");
      return;
    }
    lastSequence_ = sequence;
    for (unsigned channel = 0; channel < 6; ++channel) lastTargetUs_[channel] = microseconds[channel];
    lastTargetTime_ = completed;
    deadline_ = completed + DanceWatchdogUs;
    char message[96];
    std::snprintf(message, sizeof(message), "DANCE APPLIED token=%08lx seq=%05u us=%s",
        static_cast<unsigned long>(session_), sequence, applied);
    outputs_.Report(message);
  }

  static bool ParseMask(const char* text, unsigned& value) {
    value = 0;
    for (unsigned i = 0; i < 2; ++i) {
      const char digit = text[i];
      if (!((digit >= '0' && digit <= '9') || (digit >= 'a' && digit <= 'f'))) return false;
      value = value * 16 + static_cast<unsigned>(digit <= '9' ? digit - '0' : digit - 'a' + 10);
    }
    return value > 0 && value <= 0x3f;
  }

  Outputs& outputs_;
  bool armed_ = false;
  bool active_ = false;
  bool faulted_ = false;
  bool dropping_ = false;
  bool dancing_ = false;
  const uint32_t bootNonce_;
  uint32_t session_ = 0;
  uint32_t armCounter_ = 0;
  uint32_t armToken_ = 0;
  unsigned danceMask_ = 0;
  unsigned lastSequence_ = 0;
  unsigned lastTargetUs_[6]{};
  uint64_t lastTargetTime_ = 0;
  uint64_t danceLimit_ = 0;
  uint64_t deadline_ = 0;
  char line_[48]{};
  std::size_t length_ = 0;
};
}  // namespace ServoBench

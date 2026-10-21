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
  virtual void Report(const char* message) = 0;
};

// Single owner, no queued movements. Times are monotonic microseconds.
class Controller {
 public:
  explicit Controller(Outputs& outputs) : outputs_(outputs) { Stop("PWM FULL_OFF; disarmed"); }

  bool Faulted() const { return faulted_; }

  void Tick(uint64_t now) {
    if (active_ && now >= deadline_) {
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
      armed_ = true;
      deadline_ = now + ArmDurationUs;
      outputs_.Report("ARMED for one pulse, 10 seconds; servo must be unloaded");
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

  Outputs& outputs_;
  bool armed_ = false;
  bool active_ = false;
  bool faulted_ = false;
  bool dropping_ = false;
  uint64_t deadline_ = 0;
  char line_[48]{};
  std::size_t length_ = 0;
};
}  // namespace ServoBench

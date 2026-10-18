#pragma once

#include <cstddef>
#include <cstdint>
#include <cstring>

namespace ServoBench {
constexpr uint32_t OscillatorHz = 25000000;
constexpr unsigned Prescale = 121;
constexpr uint64_t PulseDurationUs = 500000;
constexpr uint64_t ArmDurationUs = 10000000;

inline unsigned PulseTicks(unsigned microseconds) {
  return static_cast<unsigned>((static_cast<uint64_t>(microseconds) * OscillatorHz +
      (Prescale + 1) * 500000ULL) / ((Prescale + 1) * 1000000ULL));
}

class Outputs {
 public:
  virtual ~Outputs() = default;
  virtual void Disable() = 0;
  virtual bool Prepare() = 0;
  virtual bool Pulse(unsigned channel, unsigned ticks) = 0;
  virtual void Report(const char* message) = 0;
};

// Single owner, no queued movements. Times are monotonic microseconds.
class Controller {
 public:
  explicit Controller(Outputs& outputs) : outputs_(outputs) { outputs_.Disable(); }

  void Tick(uint64_t now) {
    if (active_ && now >= deadline_) {
      Stop("DONE outputs disabled; arm again for another pulse");
    } else if (armed_ && now >= deadline_) {
      Stop("ARM expired");
    }
  }

  void Fault() {
    faulted_ = true;
    Stop("FAULT outputs disabled; restart required");
  }

  void Feed(char byte, uint64_t now) {
    Tick(now);
    // Emergency stop is recognized even inside an incomplete/overflowed line.
    if (byte == '!') {
      Stop("STOP outputs disabled");
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
      Stop("REJECT invalid/overlong line; outputs disabled");
      length_ = 0;
      dropping_ = true;
      return;
    }
    line_[length_++] = byte;
  }

 private:
  void Stop(const char* message) {
    outputs_.Disable();  // No I2C transfer may delay OE HIGH.
    active_ = false;
    armed_ = false;
    outputs_.Report(message);
  }

  void Command(uint64_t now) {
    if (std::strcmp(line_, "stop") == 0) {
      Stop("STOP outputs disabled");
      return;
    }
    if (std::strcmp(line_, "help") == 0) {
      outputs_.Report("arm | pulse <channel 0-5> <us 1400-1600> | stop | ! | help");
      return;
    }
    if (faulted_) {
      Stop("FAULT restart required");
      return;
    }
    if (active_) {
      Stop("REJECT busy; outputs disabled");
      return;
    }
    if (std::strcmp(line_, "arm") == 0) {
      Stop("Preparing PCA with OE HIGH");
      if (!outputs_.Prepare()) {
        Fault();
        return;
      }
      armed_ = true;
      deadline_ = now + ArmDurationUs;
      outputs_.Report("ARMED for one pulse, 10 seconds; servo must be unloaded");
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
      outputs_.Report("PULSE 500 ms maximum in running control loop");
      return;
    }
    Stop("REJECT unknown command; type help");
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

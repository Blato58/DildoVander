#pragma once

#include <cstdint>

namespace kufr { namespace runtime {

// One admission deadline for input sampling, output cleanup and actuator writes.
// This is not a hard driver execution bound: IDF 6.1 can repeat the per-wait
// timeout and clear a stuck bus for up to 50ms. Actual overruns are published.
// OE shutdown never consults this budget or waits for the I2C bus mutex.
class PhysicalIoBudget final {
 public:
  static constexpr uint32_t ControlBudgetUs = 8000;
  static constexpr int StartupTransferTimeoutMs = 10;
  static constexpr int ControlTransferWaitMs = 2;
  void Begin(uint64_t nowUs) { deadlineUs_ = nowUs + ControlBudgetUs; active_ = true; }
  bool Active() const { return active_; }
  int RemainingMs(uint64_t nowUs) const {
    if (!active_) return StartupTransferTimeoutMs;
    if (nowUs >= deadlineUs_) return 0;
    // Round down: a second transfer cannot acquire a fresh full timeout.
    const auto remaining = static_cast<int>((deadlineUs_ - nowUs) / 1000);
    return remaining > ControlTransferWaitMs ? ControlTransferWaitMs : remaining;
  }
 private:
  uint64_t deadlineUs_ = 0;
  bool active_ = false;
};

} }  // namespace kufr::runtime

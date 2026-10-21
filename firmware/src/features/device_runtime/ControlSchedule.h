#pragma once

#include <algorithm>
#include <cstdint>

namespace kufr { namespace runtime {

struct ControlTiming final {
  uint32_t passes = 0;
  uint32_t skippedReleases = 0;
  uint32_t executionUs = 0;
  uint32_t maximumExecutionUs = 0;
  uint32_t latenessUs = 0;
  uint32_t maximumLatenessUs = 0;
  uint32_t gapUs = 0;
  uint32_t maximumGapUs = 0;
};

// Absolute monotonic releases. One late pass handles current state; missed
// releases are counted and discarded, never replayed as actuator work.
class ControlSchedule final {
 public:
  static constexpr uint64_t PeriodUs = 10000;
  explicit ControlSchedule(uint64_t firstReleaseUs) : nextReleaseUs_(firstReleaseUs) {}
  uint64_t NextReleaseUs() const { return nextReleaseUs_; }
  bool Begin(uint64_t nowUs) {
    if (running_ || nowUs < nextReleaseUs_) return false;
    const uint64_t late = nowUs - nextReleaseUs_;
    timing_.latenessUs = Bounded(late);
    timing_.maximumLatenessUs = std::max(timing_.maximumLatenessUs, timing_.latenessUs);
    timing_.skippedReleases = Bounded(static_cast<uint64_t>(timing_.skippedReleases) + late / PeriodUs);
    nextReleaseUs_ += (late / PeriodUs + 1) * PeriodUs;
    timing_.gapUs = timing_.passes ? Bounded(nowUs - startedAtUs_) : 0;
    timing_.maximumGapUs = std::max(timing_.maximumGapUs, timing_.gapUs);
    startedAtUs_ = nowUs;
    running_ = true;
    return true;
  }
  void Finish(uint64_t nowUs) {
    if (!running_) return;
    timing_.executionUs = Bounded(nowUs - startedAtUs_);
    timing_.maximumExecutionUs = std::max(timing_.maximumExecutionUs, timing_.executionUs);
    timing_.passes = Bounded(static_cast<uint64_t>(timing_.passes) + 1);
    if (nowUs >= nextReleaseUs_) {
      const auto missed = (nowUs - nextReleaseUs_) / PeriodUs + 1;
      timing_.skippedReleases = Bounded(static_cast<uint64_t>(timing_.skippedReleases) + missed);
      nextReleaseUs_ += missed * PeriodUs;
    }
    running_ = false;
  }
  ControlTiming Timing() const { return timing_; }
 private:
  static uint32_t Bounded(uint64_t value) { return value > UINT32_MAX ? UINT32_MAX : static_cast<uint32_t>(value); }
  uint64_t nextReleaseUs_;
  uint64_t startedAtUs_ = 0;
  bool running_ = false;
  ControlTiming timing_;
};

} }  // namespace kufr::runtime

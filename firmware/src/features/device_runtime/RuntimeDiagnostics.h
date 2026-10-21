#pragma once

#include <array>
#include <atomic>
#include <cstddef>
#include "ControlSchedule.h"

namespace kufr { namespace runtime {

enum class ActuatorOwner : uint32_t {
  Idle, Recovery, Maintenance, Manual, Dance, DeviceShow, OfflineShow, Presentation, StopPending
};
struct RuntimeSnapshot final {
  ControlTiming timing;
  ActuatorOwner owner = ActuatorOwner::Recovery;
  uint32_t maintenanceOwner = 0;
  uint32_t maximumIoTransferUs = 0;
  uint32_t maximumPassBusUs = 0;
  uint32_t ioLockRefusals = 0;
  uint32_t ioBudgetRefusals = 0;
  uint32_t coalescedLedFrames = 0;
  uint32_t overBudgetPasses = 0;
};

// One publisher; bounded readers either obtain one complete immutable copy or
// retry on their next request. 32-bit atomics avoid a hidden 64-bit lock on S3.
class RuntimeDiagnostics final {
 public:
  void Publish(const RuntimeSnapshot& snapshot) {
    generation_.fetch_add(1, std::memory_order_acq_rel);
    const auto& t = snapshot.timing;
    const std::array<uint32_t, 16> values{{t.passes, t.skippedReleases, t.executionUs,
        t.maximumExecutionUs, t.latenessUs, t.maximumLatenessUs, t.gapUs,
        t.maximumGapUs, static_cast<uint32_t>(snapshot.owner), snapshot.maintenanceOwner,
        snapshot.maximumIoTransferUs, snapshot.maximumPassBusUs, snapshot.ioLockRefusals,
        snapshot.ioBudgetRefusals, snapshot.coalescedLedFrames, snapshot.overBudgetPasses}};
    for (size_t i = 0; i < values.size(); ++i) words_[i].store(values[i]);
    generation_.fetch_add(1, std::memory_order_release);
  }
  bool Read(RuntimeSnapshot& snapshot) const {
    const auto before = generation_.load(std::memory_order_acquire);
    if (!before || (before & 1)) return false;
    std::array<uint32_t, 16> values{};
    for (size_t i = 0; i < values.size(); ++i) values[i] = words_[i].load();
    if (before != generation_.load(std::memory_order_acquire)) return false;
    snapshot = {{values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7]},
        static_cast<ActuatorOwner>(values[8]), values[9], values[10], values[11], values[12], values[13], values[14], values[15]};
    return true;
  }
 private:
  std::atomic<uint32_t> generation_{0};
  std::array<std::atomic<uint32_t>, 16> words_{};
};

} }

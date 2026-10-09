#pragma once
#include <cstdint>

namespace kufr { namespace ota {

enum class BootDecision { Wait, Confirm, Rollback };
class OtaPolicy final {
 public:
  void Start(uint64_t nowMs, bool pendingVerification);
  bool CheckDue(uint64_t nowMs, bool networkReady, bool safeIdle) const;
  void Checked(uint64_t nowMs, bool success);
  bool SetCheckIntervalSeconds(uint32_t seconds);
  BootDecision BootHealth(uint64_t nowMs, bool healthy);
  uint64_t NextCheck() const { return nextCheck_; }
  static constexpr uint64_t CheckIntervalMs = 60 * 60 * 1000;
  static constexpr uint64_t BootDeadlineMs = 120000;
  static constexpr uint64_t StableHealthMs = 5000;
 private:
  uint64_t nextCheck_ = 0;
  uint64_t bootDeadline_ = 0;
  uint64_t healthySince_ = 0;
  uint64_t intervalMs_ = CheckIntervalMs;
  unsigned failures_ = 0;
  bool pending_ = false;
  bool healthy_ = false;
};
} }

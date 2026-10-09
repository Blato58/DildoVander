#include "OtaPolicy.h"

namespace kufr { namespace ota {
void OtaPolicy::Start(uint64_t nowMs, bool pendingVerification) {
  pending_ = pendingVerification;
  healthy_ = false;
  failures_ = 0;
  nextCheck_ = nowMs + 30000;
  bootDeadline_ = nowMs + BootDeadlineMs;
}
bool OtaPolicy::CheckDue(uint64_t nowMs, bool networkReady, bool safeIdle) const {
  return !pending_ && networkReady && safeIdle && nowMs >= nextCheck_;
}
void OtaPolicy::Checked(uint64_t nowMs, bool success) {
  if (success) {
    failures_ = 0;
    nextCheck_ = nowMs + intervalMs_;
  } else {
    // 30s, 60s, 120s ... up to 32min; no tight retry loops during an outage.
    if (failures_ < 6) ++failures_;
    nextCheck_ = nowMs + (30000ULL << (failures_ - 1));
  }
}
bool OtaPolicy::SetCheckIntervalSeconds(uint32_t seconds) {
  if (seconds < 60 || seconds > 86400) return false;
  intervalMs_ = static_cast<uint64_t>(seconds) * 1000;
  return true;
}
BootDecision OtaPolicy::BootHealth(uint64_t nowMs, bool healthy) {
  if (!pending_) return BootDecision::Wait;
  if (nowMs >= bootDeadline_) { pending_ = false; return BootDecision::Rollback; }
  if (!healthy) { healthy_ = false; return BootDecision::Wait; }
  if (!healthy_) { healthy_ = true; healthySince_ = nowMs; }
  if (nowMs - healthySince_ < StableHealthMs) return BootDecision::Wait;
  pending_ = false;
  return BootDecision::Confirm;
}
} }

#include "LocalControlSession.h"

#include "../conversation/ConversationStartCodec.h"
#include "../device_control/DeviceControlRuntime.h"

namespace {

constexpr uint64_t HelloDeadlineMs = 10000;

bool SameUuid(const std::string& left, const std::string& right) {
  std::string normalizedLeft;
  std::string normalizedRight;
  return NormalizeConversationUuid(left, normalizedLeft) &&
         NormalizeConversationUuid(right, normalizedRight) &&
         normalizedLeft == normalizedRight;
}

}  // namespace

LocalControlSession::LocalControlSession(
    DeviceControlRuntime& runtime, const std::string& deviceId,
    const std::string& bootId, uint64_t connectedAtMs)
    : runtime_(runtime),
      deviceId_(deviceId),
      bootId_(bootId),
      connectedAtMs_(connectedAtMs) {}

bool LocalControlSession::Hello(
    uint32_t protocolVersion, const std::string& deviceId, uint64_t nowMs) {
  if (disconnected_ || helloComplete_ || protocolVersion != 1 ||
      deviceId != deviceId_ || nowMs < connectedAtMs_ ||
      nowMs - connectedAtMs_ > HelloDeadlineMs) {
    return false;
  }
  helloComplete_ = true;
  return true;
}

bool LocalControlSession::Acquire(
    const std::string& bootId, const std::string& leaseId, uint64_t nowMs) {
  std::string normalizedLeaseId;
  if (disconnected_ || !helloComplete_ || !leaseId_.empty() ||
      !SameUuid(bootId, bootId_) ||
      !NormalizeConversationUuid(leaseId, normalizedLeaseId) ||
      !runtime_.AcquireManualControl(leaseId, nowMs)) {
    return false;
  }
  leaseId_ = leaseId;
  return true;
}

bool LocalControlSession::Heartbeat(
    const std::string& bootId, const std::string& leaseId, uint64_t nowMs) {
  if (!MatchesOwnedLease(bootId, leaseId)) {
    return false;
  }
  if (!runtime_.RenewManualLease(leaseId_, nowMs)) {
    leaseId_.clear();
    return false;
  }
  return true;
}

bool LocalControlSession::OwnsLiveLease(
    const std::string& bootId, const std::string& leaseId,
    uint64_t nowMs) const {
  return MatchesOwnedLease(bootId, leaseId) &&
         runtime_.OwnsLiveManualLease(leaseId_, nowMs);
}

bool LocalControlSession::Release(
    const std::string& bootId, const std::string& leaseId,
    SelectionStep& step) {
  if (!MatchesOwnedLease(bootId, leaseId)) {
    return false;
  }
  const bool released = runtime_.DisconnectManualControl(leaseId_, step);
  leaseId_.clear();
  return released;
}

void LocalControlSession::Disconnect(SelectionStep& step) {
  if (disconnected_) {
    return;
  }
  disconnected_ = true;
  helloComplete_ = false;
  if (!leaseId_.empty()) {
    runtime_.DisconnectManualControl(leaseId_, step);
    leaseId_.clear();
  }
}

bool LocalControlSession::MatchesOwnedLease(
    const std::string& bootId, const std::string& leaseId) const {
  return !disconnected_ && helloComplete_ && !leaseId_.empty() &&
         SameUuid(bootId, bootId_) && SameUuid(leaseId, leaseId_);
}

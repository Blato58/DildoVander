#include "TurnPollSchedule.h"

namespace {

constexpr uint64_t FastPollIntervalMs = 500;
constexpr uint64_t SlowPollIntervalMs = 1000;
constexpr uint64_t SlowPollAfterMs = 3000;

}  // namespace

void TurnPollSchedule::Begin(uint64_t turnStartedAtMs,
                             uint64_t operationAcceptedAtMs) {
  if (operationAcceptedAtMs < turnStartedAtMs ||
      operationAcceptedAtMs - turnStartedAtMs >= DeadlineMs) {
    Cancel();
    return;
  }
  startedAtMs_ = turnStartedAtMs;
  nextPollAtMs_ = operationAcceptedAtMs + FastPollIntervalMs;
  active_ = true;
  inFlight_ = false;
}

bool TurnPollSchedule::IsDue(uint64_t nowMs) const {
  return active_ && !inFlight_ && !IsExpired(nowMs) &&
         nowMs >= nextPollAtMs_;
}

bool TurnPollSchedule::IsExpired(uint64_t nowMs) const {
  return active_ &&
         (nowMs < startedAtMs_ || nowMs - startedAtMs_ >= DeadlineMs);
}

bool TurnPollSchedule::MarkStarted(uint64_t nowMs) {
  if (!IsDue(nowMs)) {
    return false;
  }
  inFlight_ = true;
  return true;
}

void TurnPollSchedule::MarkCompleted(uint64_t nowMs, bool stillPending) {
  if (!active_ || !inFlight_) {
    return;
  }
  inFlight_ = false;
  if (!stillPending || IsExpired(nowMs)) {
    Cancel();
    return;
  }
  const uint64_t elapsedMs = nowMs - startedAtMs_;
  nextPollAtMs_ = nowMs +
                  (elapsedMs >= SlowPollAfterMs ? SlowPollIntervalMs
                                                 : FastPollIntervalMs);
}

void TurnPollSchedule::Cancel() {
  active_ = false;
  inFlight_ = false;
  startedAtMs_ = 0;
  nextPollAtMs_ = 0;
}

bool TurnPollSchedule::IsActive() const { return active_; }

#pragma once

#include <stdint.h>

// Serializes polling and enforces the text-turn deadline from a monotonic clock.
class TurnPollSchedule final {
 public:
  static constexpr uint64_t DeadlineMs = 45000;

  void Begin(uint64_t turnStartedAtMs, uint64_t operationAcceptedAtMs);
  bool IsDue(uint64_t nowMs) const;
  bool IsExpired(uint64_t nowMs) const;
  bool MarkStarted(uint64_t nowMs);
  void MarkCompleted(uint64_t nowMs, bool stillPending);
  void Cancel();
  bool IsActive() const;

 private:
  uint64_t startedAtMs_ = 0;
  uint64_t nextPollAtMs_ = 0;
  bool active_ = false;
  bool inFlight_ = false;
};

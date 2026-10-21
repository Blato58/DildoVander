#pragma once

#include <stdint.h>
#include <string>

// The server must learn this boot before it can trust a physical-idle report.
// Registration is safe while busy; retiring the previous boot is not.
template <typename RegisterBoot, typename ReportIdle>
bool ReconcileCustomerBoot(bool ownerIdle, RegisterBoot registerBoot, ReportIdle reportIdle) {
  if (!registerBoot()) return false;
  return ownerIdle && reportIdle();
}

// Durable discovery can retire an accepted operation after a lost reply. It
// cannot reopen its provider, change its owner, or authorize a different boot.
inline bool CanRetireUnknownCustomerStart(const std::string& localState,
    const std::string& localBoot, const std::string& localOperation, bool voiceBusy,
    const std::string& remoteBoot, const std::string& remoteOperation) {
  return localState == "recovering" && !voiceBusy && !localOperation.empty() &&
      localBoot == remoteBoot && localOperation == remoteOperation;
}

// A cached challenge is useful only for the exact currently published runtime.
// Registration hides the previous challenge until a current replacement lands.
inline bool IsCustomerChallengeCurrent(bool present, bool registering, bool snapshotMatches,
    uint64_t nowMs, uint64_t createdAtMs, uint64_t freshnessMs) {
  return present && !registering && snapshotMatches && nowMs >= createdAtMs &&
      nowMs - createdAtMs < freshnessMs;
}

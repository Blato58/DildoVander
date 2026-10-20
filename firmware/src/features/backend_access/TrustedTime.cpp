#include "TrustedTime.h"

namespace {

constexpr uint64_t BuildDateToleranceSeconds = 2u * 24u * 60u * 60u;
constexpr uint64_t BackwardToleranceSeconds = 5u * 60u;
constexpr uint64_t PersistIntervalSeconds = 24u * 60u * 60u;

}  // namespace

TrustedTime::TrustedTime(LastTrustedTimeStore& store,
                         uint64_t buildEpochSeconds)
    : store_(store), buildEpochSeconds_(buildEpochSeconds) {
  const LastTrustedTimeRead result = store_.Read(lastTrustedEpochSeconds_);
  healthy_ = result != LastTrustedTimeRead::Failed;
  if (result == LastTrustedTimeRead::Missing) {
    lastTrustedEpochSeconds_ = 0;
  }
}

bool TrustedTime::CheckAndRecord(uint64_t nowEpochSeconds) {
  if (!healthy_ || nowEpochSeconds == 0 ||
      (nowEpochSeconds < buildEpochSeconds_ &&
       buildEpochSeconds_ - nowEpochSeconds > BuildDateToleranceSeconds) ||
      (nowEpochSeconds < lastTrustedEpochSeconds_ &&
       lastTrustedEpochSeconds_ - nowEpochSeconds >
           BackwardToleranceSeconds)) {
    return false;
  }

  if (lastTrustedEpochSeconds_ == 0 ||
      (nowEpochSeconds > lastTrustedEpochSeconds_ &&
       nowEpochSeconds - lastTrustedEpochSeconds_ >=
           PersistIntervalSeconds)) {
    if (!store_.Write(nowEpochSeconds)) {
      healthy_ = false;
      return false;
    }
    lastTrustedEpochSeconds_ = nowEpochSeconds;
  }
  return true;
}

bool TrustedTime::IsHealthy() const { return healthy_; }

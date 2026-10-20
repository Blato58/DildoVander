#include "DeviceStorageRuntime.h"

namespace {
bool CompleteBinding(const JournalEvidenceInventory& inventory, const std::string& deviceId) {
  return inventory.origin == JournalInventoryOrigin::Valid && inventory.complete &&
      !inventory.ambiguous && inventory.generation != 0 && inventory.deviceId == deviceId &&
      inventory.count <= inventory.Capacity;
}
} // namespace

DeviceStorageRuntime::DeviceStorageRuntime(JournalStorage& journal, JournalStorage& marker,
    JournalStorage& online, JournalStorage& offline, const JournalIdentityView& identity, Policy policy)
    : identityView_(identity), policy_(policy), journal_(journal), identity_(marker, journal_),
      online_(online), offline_(offline), journalJobs_(journal_, arbiter_, this),
      evidenceJobs_(journal_, online_, offline_, arbiter_, this),
      reconciliation_(journal_, identity_, online_, offline_, arbiter_), control_(journalJobs_, arbiter_) {
  // Startup publication cannot authorize work before explicit inspection.
  arbiter_.CancelAdmissions();
}

bool DeviceStorageRuntime::AllowsRecovery() const {
  return bindingSafe_.load() && sourcesSafe_.load() && evidenceJobs_.IsHealthy();
}

bool DeviceStorageRuntime::AllowsAdmission() const {
  return AllowsRecovery() && reconciled_.load() &&
      reconciledCancellation_.load() == arbiter_.Cancellation();
}

bool DeviceStorageRuntime::ReconcileBeforeResume(uint32_t cancellation) {
  if (!AllowsRecovery() || cancellation != arbiter_.Cancellation()) return false;
  if (reconciled_.load() && reconciledCancellation_.load() == cancellation) return true;
  requestedCancellation_.store(cancellation);
  return false;
}

bool DeviceStorageRuntime::ReadSourceAuthority() {
  return online_.ReadCommitted(onlineInventory_) && offline_.ReadCommitted(offlineInventory_) &&
      CompleteBinding(onlineInventory_, identityView_.deviceId) &&
      CompleteBinding(offlineInventory_, identityView_.deviceId);
}

void DeviceStorageRuntime::SourceFailure() {
  if (sourcesSafe_.exchange(false)) arbiter_.CancelAdmissions();
  reconciled_.store(false);
  state_.store(State::InventoryUnavailable);
}

bool DeviceStorageRuntime::Boot() {
  if (bootAttempted_) return false;
  bootAttempted_ = true;
  if (!identity_.HasEstablishedBinding(identityView_)) {
    state_.store(State::IdentityUnavailable);
    return false;
  }
  bindingSafe_.store(true);
  if (!evidenceJobs_.OpenAndPublish() || !evidenceJobs_.RecoverProvenCompletions() || !ReadSourceAuthority()) {
    SourceFailure();
    return false;
  }
  sourcesSafe_.store(true);
  dirty_ = true;
  requestedCancellation_.store(arbiter_.Cancellation());
  if (journal_.HasPending()) {
    state_.store(State::InspectionRequired);
    return false;
  }
  return Reconcile();
}

bool DeviceStorageRuntime::Reconcile() {
  if (!AllowsRecovery()) return false;
  if (journal_.HasPending() || journal_.HasPresentationReservation()) {
    if (arbiter_.AdmissionsStopped()) state_.store(State::InspectionRequired);
    return false;
  }
  const uint32_t cancellation = arbiter_.Cancellation();
  const auto result = reconciliation_.Reconcile(identityView_, policy_ == Policy::BoundedCompaction);
  using Result = JournalReconciliation::Result;
  if (result == Result::Busy || result == Result::PresentationReserved || result == Result::PendingOperation)
    return false;
  if (result != Result::NotNeeded && result != Result::Reconciled && result != Result::Compacted &&
      result != Result::CapacityPinned && result != Result::EpochExhausted) {
    reconciled_.store(false);
    if (!journal_.IsHealthy() || !online_.IsHealthy() || !offline_.IsHealthy()) SourceFailure();
    else state_.store(State::ReconciliationFailed);
    return false;
  }
  if (!journalJobs_.RefreshPublication()) return false;
  if (result == Result::Compacted) {
    ++compactions_;
    prunePending_ = true;
  }
  // Stop can advance while the worker writes; it invalidates this authority.
  if (cancellation != arbiter_.Cancellation()) {
    reconciled_.store(false);
    requestedCancellation_.store(arbiter_.Cancellation());
    return false;
  }
  dirty_ = false;
  // Also retry a pruning transaction interrupted after a prior epoch commit.
  // Reopening may find an already compacted journal with its old ledger intact.
  prunePending_ = true;
  requestedCancellation_.store(0);
  reconciledCancellation_.store(cancellation);
  reconciled_.store(true);
  state_.store(result == Result::CapacityPinned || result == Result::EpochExhausted
      ? State::CapacityBlocked : State::Ready);
  return true;
}

bool DeviceStorageRuntime::PruneRetired() {
  if (!online_.PruneRetired(journal_, arbiter_)) {
    if (!online_.IsHealthy()) SourceFailure();
    return false; // A queued consumer may have acquired the shared barrier.
  }
  // Reload the runtime publication after the typed durable pruning commit.
  // This is never an automatic reopen of a failed write: both source owners
  // must still be healthy, and a failure poisons this runtime permanently.
  if (!evidenceJobs_.OpenAndPublish() || !ReadSourceAuthority()) {
    if (!evidenceJobs_.Busy()) SourceFailure();
    return false;
  }
  prunePending_ = false;
  return true;
}

bool DeviceStorageRuntime::RunOne() {
  if (!bootAttempted_) return false;
  // These channels share one worker and one arbiter. Always burn cancelled
  // volatile journal capabilities before evaluating a new reference intent.
  const bool journalWork = journalJobs_.RunOne();
  const bool evidenceWork = evidenceJobs_.RunOne();
  dirty_ |= journalWork || evidenceWork;
  // Even a poisoned runtime drains already-reserved result slots. Gate checks
  // prevent any further I/O; abandoning those slots would orphan cancellation.
  if (!bindingSafe_.load() || !sourcesSafe_.load()) return journalWork || evidenceWork;
  if (!journal_.IsHealthy() || !evidenceJobs_.IsHealthy() ||
      (evidenceWork && !ReadSourceAuthority())) {
    SourceFailure();
    return journalWork || evidenceWork;
  }
  if (!journalJobs_.Busy() && !evidenceJobs_.Busy()) {
    if (prunePending_ && !PruneRetired()) return journalWork || evidenceWork;
    if (dirty_ || requestedCancellation_.load() != 0 ||
        reconciledCancellation_.load() != arbiter_.Cancellation()) {
      return Reconcile() || journalWork || evidenceWork;
    }
  }
  return journalWork || evidenceWork;
}

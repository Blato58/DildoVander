#include "JournalReconciliation.h"

#include <limits>

#include "JournalIdentityOwner.h"

namespace {

class BarrierLease final {
 public:
  explicit BarrierLease(JournalReconciliationBarrier& barrier) : barrier_(barrier) {}
  ~BarrierLease() { barrier_.Leave(); }
  BarrierLease(const BarrierLease&) = delete;
  BarrierLease& operator=(const BarrierLease&) = delete;

 private:
  JournalReconciliationBarrier& barrier_;
};

}  // namespace

JournalReconciliation::JournalReconciliation(
    PersistentCommandJournal& journal, const JournalIdentityOwner& identity,
    JournalEvidenceSource& online, JournalEvidenceSource& offline,
    JournalReconciliationBarrier& barrier)
    : journal_(journal), identity_(identity), online_(online), offline_(offline), barrier_(barrier) {}

void JournalReconciliation::ResetInventory(JournalEvidenceInventory& inventory) {
  // Reset only metadata. References outside the next committed count are never
  // read, and no full inventory temporary is allocated on the control stack.
  inventory.origin = JournalInventoryOrigin::Unavailable;
  inventory.deviceId.clear();
  inventory.generation = 0;
  inventory.complete = false;
  inventory.ambiguous = false;
  inventory.count = 0;
}

JournalReconciliation::Result JournalReconciliation::ValidateInventory(
    const JournalEvidenceInventory& inventory, const std::string& deviceId, uint64_t epoch) {
  if (inventory.origin == JournalInventoryOrigin::Missing) return Result::InventoryMissing;
  if (inventory.origin != JournalInventoryOrigin::Valid) return Result::InventoryUnavailable;
  if (!inventory.complete) return Result::InventoryIncomplete;
  if (inventory.ambiguous) return Result::InventoryAmbiguous;
  if (inventory.deviceId != deviceId) return Result::InventoryWrongDevice;
  if (inventory.generation == 0) return Result::InventoryInvalidGeneration;
  if (inventory.count > JournalEvidenceInventory::Capacity) return Result::InventoryOverflow;
  for (size_t reference = 0; reference < inventory.count; ++reference) {
    const auto& identity = inventory.references[reference];
    if (!ValidJournalOperationIdentity(identity) || identity.epoch > epoch) return Result::InvalidReference;
    bool found = false;
    for (size_t entry = 0; entry < historyCount_; ++entry) {
      if (!SameJournalOperationIdentity(identity, history_[entry].identity)) continue;
      referenced_[entry] = true;
      found = true;
      break;
    }
    if (!found) return Result::ReferenceUnavailable;
  }
  return Result::Reconciled;
}

JournalReconciliation::Result JournalReconciliation::Reconcile(
    const JournalIdentityView& identity, bool compactWhenFull) {
  if (!barrier_.TryEnter()) return Result::Busy;
  BarrierLease held(barrier_);
  const auto validation = identity_.Validate(identity);
  if (validation == JournalIdentityOwner::Validation::JournalUnavailable) return Result::JournalUnavailable;
  if (validation == JournalIdentityOwner::Validation::InspectionRequired && journal_.HasPending()) return Result::PendingOperation;
  if (validation != JournalIdentityOwner::Validation::Ready) return Result::IdentityNotReady;
  if (!journal_.IsHealthy() || journal_.Origin() != PersistentCommandJournal::StateOrigin::Scoped ||
      journal_.ScopedDeviceId() != identity.deviceId || journal_.CurrentEpoch() == 0) return Result::JournalUnavailable;
  if (journal_.HasPending()) return Result::PendingOperation;

  historyCount_ = journal_.HistoryCount();
  if (historyCount_ > history_.size()) return Result::HistoryUnavailable;
  referenced_.fill(false);
  for (size_t entry = 0; entry < historyCount_; ++entry) {
    if (!journal_.ReadHistoryEntry(entry, history_[entry]) ||
        !ValidJournalOperationIdentity(history_[entry].identity) ||
        history_[entry].identity.epoch > journal_.CurrentEpoch()) return Result::HistoryUnavailable;
  }

  ResetInventory(onlineInventory_);
  ResetInventory(offlineInventory_);
  const bool onlineRead = online_.ReadCommitted(onlineInventory_);
  const bool offlineRead = offline_.ReadCommitted(offlineInventory_);
  if (!onlineRead || !offlineRead) return Result::InventoryUnavailable;
  Result status = ValidateInventory(onlineInventory_, identity.deviceId, journal_.CurrentEpoch());
  if (status != Result::Reconciled) return status;
  status = ValidateInventory(offlineInventory_, identity.deviceId, journal_.CurrentEpoch());
  if (status != Result::Reconciled) return status;

  // A prior interrupted attempt may have released a now-referenced entry.
  // Re-pin every exact dependency before considering any destructive change.
  bool repaired = false;
  size_t referencedCount = 0;
  for (size_t entry = 0; entry < historyCount_; ++entry) {
    if (!referenced_[entry]) continue;
    ++referencedCount;
    if (history_[entry].evidencePinned) continue;
    const auto& operation = history_[entry].identity;
    if (!journal_.RetainEvidencePin(operation.epoch, operation.operationId,
                                    operation.compartment, operation.fingerprint)) return Result::PinRepairFailed;
    history_[entry].evidencePinned = true;
    repaired = true;
  }
  if (!compactWhenFull || historyCount_ < history_.size()) return repaired ? Result::Reconciled : Result::NotNeeded;
  if (journal_.CurrentEpoch() == std::numeric_limits<uint64_t>::max()) return Result::EpochExhausted;
  if (referencedCount == historyCount_) return Result::CapacityPinned;

  for (size_t entry = 0; entry < historyCount_; ++entry) {
    if (referenced_[entry] || !history_[entry].evidencePinned) continue;
    const auto& operation = history_[entry].identity;
    if (!journal_.ReleaseEvidencePin(operation.epoch, operation.operationId,
                                     operation.compartment, operation.fingerprint)) return Result::PinReleaseFailed;
  }
  if (!journal_.AdvanceEpochAndCompact()) return Result::CompactionFailed;
  return Result::Compacted;
}

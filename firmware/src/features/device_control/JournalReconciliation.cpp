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
  if (journal_.HasPresentationReservation()) return Result::PresentationReserved;

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

  // Validate all dependencies first, then commit the entire pin/epoch decision
  // once. A lost ACK exposes either the complete old or complete new journal;
  // no intermediate unpinned state or per-entry NVS write loop is needed.
  bool repaired = false;
  size_t referencedCount = 0;
  for (size_t entry = 0; entry < historyCount_; ++entry) {
    if (!referenced_[entry]) continue;
    ++referencedCount;
    if (history_[entry].evidencePinned) continue;
    repaired = true;
  }
  const bool full = compactWhenFull && historyCount_ == history_.size();
  const bool exhausted = journal_.CurrentEpoch() == std::numeric_limits<uint64_t>::max();
  const bool pinned = referencedCount == historyCount_;
  const bool compact = full && !exhausted && !pinned;
  if (compact || repaired) {
    if (!journal_.CommitEvidenceReconciliation(journal_.CurrentEpoch(), history_,
        historyCount_, referenced_, compact)) return compact ? Result::CompactionFailed : Result::PinRepairFailed;
  }
  if (compact) return Result::Compacted;
  if (full && exhausted) return Result::EpochExhausted;
  if (full && pinned) return Result::CapacityPinned;
  return repaired ? Result::Reconciled : Result::NotNeeded;
}

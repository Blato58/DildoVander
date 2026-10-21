#pragma once

#include <array>

#include "JournalEvidence.h"
#include "PersistentCommandJournal.h"

class JournalIdentityOwner;
struct JournalIdentityView;

// Foundation only: production must first put all reference producers, durable
// inventory commits and persisted-journal readers behind the same barrier.
class JournalReconciliation final {
 public:
  enum class Result : uint8_t {
    Busy,
    IdentityNotReady,
    JournalUnavailable,
    PendingOperation,
    PresentationReserved,
    InventoryMissing,
    InventoryUnavailable,
    InventoryIncomplete,
    InventoryAmbiguous,
    InventoryWrongDevice,
    InventoryInvalidGeneration,
    InventoryOverflow,
    HistoryUnavailable,
    InvalidReference,
    ReferenceUnavailable,
    PinRepairFailed,
    CapacityPinned,
    EpochExhausted,
    CompactionFailed,
    NotNeeded,
    Reconciled,
    Compacted,
  };

  JournalReconciliation(PersistentCommandJournal& journal,
                        const JournalIdentityOwner& identity,
                        JournalEvidenceSource& online, JournalEvidenceSource& offline,
                        JournalReconciliationBarrier& barrier);

  // Only Compacted authorizes publication of the journal's newly committed
  // epoch. Any failed journal write requires authoritative storage/owner reopen.
  // Reconciled, CapacityPinned and EpochExhausted may follow conservative pin
  // repairs. Each attempt performs at most one journal replacement. Only
  // Compacted can discard unreferenced history and advance.
  Result Reconcile(const JournalIdentityView& identity, bool compactWhenFull = true);

 private:
  static void ResetInventory(JournalEvidenceInventory& inventory);
  Result ValidateInventory(const JournalEvidenceInventory& inventory,
                           const std::string& deviceId, uint64_t epoch);

  PersistentCommandJournal& journal_;
  const JournalIdentityOwner& identity_;
  JournalEvidenceSource& online_;
  JournalEvidenceSource& offline_;
  JournalReconciliationBarrier& barrier_;
  // These bounded snapshots live with the owner, not on the ESP control stack.
  JournalEvidenceInventory onlineInventory_;
  JournalEvidenceInventory offlineInventory_;
  std::array<JournalHistoryEntry, PersistentCommandJournal::HistoryCapacity> history_{};
  std::array<bool, PersistentCommandJournal::HistoryCapacity> referenced_{};
  size_t historyCount_ = 0;
};

#pragma once

#include <atomic>

#include "AsyncCommandJournal.h"
#include "EvidenceStorageOwner.h"
#include "JournalIdentityOwner.h"
#include "JournalReconciliation.h"
#include "../keepsake/KeepsakeCommittedInventory.h"
#include "../offline_show/OfflineCommittedInventory.h"

// Concrete storage composition. Construct before control/network owners start;
// Boot and RunOne belong to ONE storage worker. No method establishes identity.
// Production must migrate every consumer before selecting this composition.
class DeviceStorageRuntime final : public JournalAdmissionGate {
 public:
  enum class Policy : uint8_t { ReconcileOnly, BoundedCompaction };
  enum class State : uint8_t {
    Uninitialized, IdentityUnavailable, InventoryUnavailable,
    InspectionRequired, ReconciliationFailed, Ready, CapacityBlocked
  };
  DeviceStorageRuntime(JournalStorage& journal, JournalStorage& marker,
      JournalStorage& online, JournalStorage& offline,
      const JournalIdentityView& identity, Policy policy = Policy::ReconcileOnly);
  DeviceStorageRuntime(const DeviceStorageRuntime&) = delete;
  DeviceStorageRuntime& operator=(const DeviceStorageRuntime&) = delete;

  bool Boot();
  bool RunOne();
  CommandJournal& ControlJournal() { return control_; }
  EvidenceStorageOwner& EvidenceRequests() { return evidenceJobs_; }
  State CurrentState() const { return state_.load(); }
  uint64_t Compactions() const { return compactions_.load(); }
  // Thread-safe RAM mailbox observation; never inspect the control-owned facade
  // from a producer/network thread. The result lease remains busy until adopted.
  bool HasPendingJournalWork() const { return journalJobs_.Busy(); }
  bool ReadJournalSnapshot(JournalSnapshot& snapshot) const {
    return journalJobs_.TryReadPublished(snapshot);
  }
  bool AllowsAdmission() const override;
  bool AllowsRecovery() const override;
  bool ReconcileBeforeResume(uint32_t cancellation) override;
  InspectedRecoveryProgress AdvanceRecoveryResume(uint32_t cancellation) override;

 private:
  bool ReadSourceAuthority();
  bool Reconcile();
  bool PruneRetired();
  void SourceFailure();

  const JournalIdentityView identityView_;
  const Policy policy_;
  std::atomic<State> state_{State::Uninitialized};
  std::atomic<bool> bindingSafe_{false}, sourcesSafe_{false}, reconciled_{false};
  std::atomic<uint32_t> reconciledCancellation_{0}, requestedCancellation_{0};
  std::atomic<uint64_t> compactions_{0};
  bool bootAttempted_ = false, dirty_ = false, prunePending_ = false;
  PersistentCommandJournal journal_;
  JournalIdentityOwner identity_;
  KeepsakeCommittedInventory online_;
  kufr::offline::OfflineCommittedInventory offline_;
  JournalTransactionArbiter arbiter_;
  JournalStorageOwner journalJobs_;
  EvidenceStorageOwner evidenceJobs_;
  JournalReconciliation reconciliation_;
  AsyncCommandJournal control_;
  // Worker-owned scratch lives with the object, never on the actuator stack.
  JournalEvidenceInventory onlineInventory_, offlineInventory_;
};

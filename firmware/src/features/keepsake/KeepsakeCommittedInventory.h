#pragma once

#include <cstddef>
#include <cstdint>
#include <string>

#include "KeepsakeOutbox.h"
#include "../device_control/JournalStorage.h"

class PersistentCommandJournal;

// Only this owner's committed bytes can authorize an online inventory. A RAM
// outbox, missing blob or legacy unbound payload never proves established empty.
class KeepsakeCommittedInventory final : public JournalServiceInventory {
 public:
  static constexpr size_t MaximumRecordSize = 32768;
  explicit KeepsakeCommittedInventory(JournalStorage& storage);
  bool Reopen();
  bool IsHealthy() const { return healthy_; }
  bool IsBound() const { return healthy_ && bound_; }
  bool ReadState(KeepsakeOutboxState& state) const;
  bool ReadCommitted(JournalEvidenceInventory& inventory) const override;
  bool Commit(const KeepsakeOutboxState& state);
  bool Establish(const std::string& deviceId, bool serviceAuthorized);
  bool EstablishBinding(const std::string& deviceId, bool serviceAuthorized) override {
    return Establish(deviceId, serviceAuthorized);
  }
  // Explicit post-compaction cleanup, under the same barrier as all producers.
  // Public Commit cannot remove durable ledger entries.
  bool PruneRetired(const PersistentCommandJournal& journal, JournalReconciliationBarrier& barrier);

 private:
  bool CommitState(const KeepsakeOutboxState& state, bool pruning);
  bool ReadBytes(std::string& bytes, bool& missing) const;
  bool CheckCommittedBytes() const;
  bool Write(const KeepsakeOutboxState& state, const std::string& payload,
             const std::string& bytes, bool bound, const std::string& deviceId, uint64_t generation);
  bool RetainsLedger(const KeepsakeOutboxState& state) const;
  bool PreservesActiveReferences(const KeepsakeOutboxState& state) const;
  static bool ValidState(const KeepsakeOutboxState& state, KeepsakeOutboxState& decoded, std::string& payload);
  static bool DecodeBound(const std::string& bytes, KeepsakeOutboxState& state, std::string& deviceId, uint64_t& generation);
  static std::string EncodeBound(const KeepsakeOutboxState& state, const std::string& deviceId, uint64_t generation);
  static bool ValidDeviceId(const std::string& deviceId);
  static bool AddReference(JournalEvidenceInventory& inventory, const JournalOperationIdentity& identity);

  JournalStorage& storage_;
  mutable bool healthy_ = false;
  bool missing_ = true, bound_ = false;
  std::string deviceId_, committedBytes_, committedPayload_;
  uint64_t generation_ = 0;
  KeepsakeOutboxState committedState_;
};

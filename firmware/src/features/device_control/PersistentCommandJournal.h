#pragma once

#include <stdint.h>

#include <array>
#include <string>

#include "CommandJournal.h"
#include "JournalStorage.h"
#include "JournalEvidence.h"
#include "JournalSnapshot.h"

class PersistentCommandJournal final : public CommandJournal {
 public:
  static constexpr size_t HistoryCapacity = 32;
  static constexpr size_t MaximumDeviceIdLength = 64;
  // Fixed header (legacy pending UUID, epoch, origin and device binding),
  // fixed entries (UUID, compartment/flags, fingerprint, epoch), then CRC32.
  static constexpr size_t RecordSize = 118 + HistoryCapacity * 54 + 4;
  enum class StateOrigin : uint8_t { Missing, Legacy, Scoped, Unavailable };

  explicit PersistentCommandJournal(JournalStorage& storage);
  // This mutable storage owner may hold one-use physical admission authority.
  PersistentCommandJournal(const PersistentCommandJournal&) = delete;
  PersistentCommandJournal& operator=(const PersistentCommandJournal&) = delete;
  PersistentCommandJournal(PersistentCommandJournal&&) = delete;
  PersistentCommandJournal& operator=(PersistentCommandJournal&&) = delete;

  bool IsHealthy() const;
  bool HasStorageFault() const override { return !IsHealthy(); }
  bool IsStorageMissing() const { return healthy_ && storageMissing_; }
  StateOrigin Origin() const;
  const std::string& ScopedDeviceId() const { return state_.deviceId; }
  uint64_t CurrentEpoch() const override;
  bool HasPending() const override;
  uint8_t PendingCompartment() const override;
  uint8_t ReadyMask() const override;
  bool Begin(const std::string& commandId, uint8_t compartment) override;
  bool BeginWithIdentity(const std::string& commandId, uint8_t compartment,
                         uint64_t fingerprint) override;
  Lookup Find(const std::string& commandId, uint8_t compartment,
              uint64_t fingerprint) const override;
  size_t HistoryCount() const { return state_.historyCount; }
  bool ReadHistoryEntry(size_t index, JournalHistoryEntry& entry) const;
  bool ExportRecord(std::array<uint8_t, RecordSize>& record) const;
  bool ExportSnapshot(JournalSnapshot& snapshot) const;
  bool PreviewScopedIdentity(const std::string& deviceId, bool factoryProvisioning,
                            std::array<uint8_t, RecordSize>& record) const;
  bool CanBeginOperation() const override;
  bool Resolve(uint8_t readyMask) override;
  bool ResolveRestored(uint8_t compartment) override;
  bool ConfirmTimedPresentation(const std::string& id, uint8_t compartment, uint64_t fingerprint) override;
  bool WasTimedPresentationCompleted(const std::string& id, uint8_t compartment) const override;
  bool WasLegacyTimedPresentationCompleted(const std::string& id,
                                           uint8_t compartment) const override;
  bool PersistReadyMask(uint8_t readyMask) override;

  // Owner-only opt-in after provisioning/reconciliation. Missing storage is
  // ambiguous (factory or lost identity) and requires explicit factory authority.
  // The integration must separately retain an established marker with device
  // identity and reject a missing established journal; this class cannot prove it.
  bool EstablishScopedIdentity(const std::string& deviceId, bool factoryProvisioning = false);
  // Atomic epoch increment plus retention of all pinned history. Never automatic.
  bool AdvanceEpochAndCompact();
  bool BeginScoped(uint64_t epoch, const std::string& id, uint8_t compartment,
                   uint64_t fingerprint) override;
  bool ReservePresentation(uint64_t epoch, const std::string& id, uint8_t compartment,
                           uint64_t fingerprint) override;
  bool HasPresentationReservation() const override;
  bool HasPresentationReservation(uint64_t epoch, const std::string& id,
                                  uint8_t compartment, uint64_t fingerprint) const override;
  bool BeginReservedPresentation(uint64_t epoch, const std::string& id,
                                 uint8_t compartment, uint64_t fingerprint) override;
  void CancelPresentationReservation() override;
  Lookup FindScoped(uint64_t epoch, const std::string& id, uint8_t compartment,
                    uint64_t fingerprint) const override;
  Lookup FindHistorical(uint64_t epoch, const std::string& id, uint8_t compartment,
                        uint64_t fingerprint) const override;
  bool ConfirmTimedPresentationScoped(uint64_t epoch, const std::string& id,
                                       uint8_t compartment, uint64_t fingerprint) override;
  bool WasTimedPresentationCompletedScoped(uint64_t epoch, const std::string& id,
                                            uint8_t compartment, uint64_t fingerprint) const override;
  // Includes retained epoch-zero legacy proof, without authorizing epoch-zero admission.
  bool WasTimedPresentationCompletedHistorical(uint64_t epoch, const std::string& id,
                                                uint8_t compartment, uint64_t fingerprint) const override;
  // Owner must first durably reconcile every outbox/evidence consumer. Exact
  // identity is mandatory; release never deletes an entry within its epoch.
  bool ReleaseEvidencePin(uint64_t epoch, const std::string& id,
                          uint8_t compartment, uint64_t fingerprint);
  bool RetainEvidencePin(uint64_t epoch, const std::string& id,
                         uint8_t compartment, uint64_t fingerprint);
  // Owner-only batch under the common barrier. The exact expected snapshot
  // fences stale reconciliation. Repairs retain existing pins; compaction
  // atomically retains only referenced entries and advances the epoch. Either
  // mode performs at most one journal replacement, never per-entry writes.
  bool CommitEvidenceReconciliation(
      uint64_t expectedEpoch,
      const std::array<JournalHistoryEntry, HistoryCapacity>& expectedHistory,
      size_t expectedCount, const std::array<bool, HistoryCapacity>& referenced,
      bool compact);
  bool IsEvidencePinned(uint64_t epoch, const std::string& id,
                        uint8_t compartment, uint64_t fingerprint) const;

 private:
  struct Entry {
    std::string id;
    uint8_t compartment = 0;
    uint64_t fingerprint = 0;
    uint64_t epoch = 0;
    bool timedCompleted = false;
    bool evidencePinned = true;
  };
  struct State {
    uint8_t readyMask = 0;
    uint8_t pendingCompartment = 0;
    std::string commandId;
    uint64_t currentEpoch = 0;
    StateOrigin origin = StateOrigin::Missing;
    std::string deviceId;
    std::array<Entry, HistoryCapacity> history{};
    uint8_t historyCount = 0;
  };

  bool BuildScopedState(const std::string& deviceId, bool factoryProvisioning, State& next) const;
  bool BeginAtEpoch(uint64_t epoch, const std::string& id, uint8_t compartment, uint64_t fingerprint);
  bool ConfirmTimedAtEpoch(uint64_t epoch, const std::string& id, uint8_t compartment, uint64_t fingerprint);
  const Entry* ExactEntry(uint64_t epoch, const std::string& id,
                          uint8_t compartment, uint64_t fingerprint) const;
  // Storage.Write MUST atomically expose old or new complete bytes. A false
  // result may have committed: poison this instance and reopen authoritative
  // storage before retrying. CRC detects corruption, not rollback or data loss.
  bool Persist(const State& next);
  static bool Decode(const uint8_t* data, size_t length, State& state);
  static void Encode(const State& state, std::array<uint8_t, RecordSize>& data);
  static bool IsCanonicalUuid(const std::string& value);
  static bool IsValidDeviceId(const std::string& value);

  JournalStorage& storage_;
  State state_;
  // Deliberately excluded from Encode/Decode: durable history alone never
  // grants authority to start a previously reserved physical presentation.
  JournalOperationIdentity presentationReservation_;
  bool healthy_ = false;
  bool storageMissing_ = false;
};

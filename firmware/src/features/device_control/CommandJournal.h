#pragma once

#include <stdint.h>

#include <string>
#include "JournalAdmissionGate.h"

// The device adapter must persist Begin before any physical output is allowed.
class CommandJournal {
 public:
  enum class Lookup { Missing, Same, Conflict, StaleEpoch, Unavailable };
  enum class StorageState { Rejected, Pending, Committed, Unknown, Superseded };
  virtual ~CommandJournal() {}
  // Synchronous ports retain Rejected as their false-return interpretation.
  // Async ports distinguish queued work from an actual storage failure.
  virtual StorageState LastStorageState() const { return StorageState::Rejected; }
  virtual bool HasPendingStorage() const { return false; }
  virtual bool HasStorageFault() const { return false; }
  virtual void PollStorage() {}
  virtual void CancelStorageAdmissions() { CancelPresentationReservation(); }
  virtual void BeginStorageRecovery() {}
  virtual bool ResumeStorageAfterRecovery() { return true; }
  virtual InspectedRecoveryProgress ResolveStorageRecovery(uint8_t readyMask) {
    if (Resolve(readyMask)) return InspectedRecoveryProgress::Succeeded;
    switch (LastStorageState()) {
      case StorageState::Pending: return InspectedRecoveryProgress::Pending;
      case StorageState::Unknown: return InspectedRecoveryProgress::Unknown;
      case StorageState::Superseded: return InspectedRecoveryProgress::Cancelled;
      default: return InspectedRecoveryProgress::Rejected;
    }
  }
  virtual InspectedRecoveryProgress AdvanceStorageRecovery() {
    if (ResumeStorageAfterRecovery()) return InspectedRecoveryProgress::Succeeded;
    return HasPendingStorage() ? InspectedRecoveryProgress::Pending : InspectedRecoveryProgress::Rejected;
  }
  virtual void CancelStorageRecovery() { CancelStorageAdmissions(); }
  virtual bool HasRecoveryStorageWork() const { return false; }

  virtual bool HasPending() const = 0;
  virtual bool CanBeginOperation() const { return !HasPending(); }
  virtual uint8_t PendingCompartment() const = 0;
  virtual uint8_t ReadyMask() const = 0;
  virtual bool Begin(const std::string& commandId, uint8_t compartment) = 0;
  virtual bool BeginWithIdentity(const std::string& commandId, uint8_t compartment,
                                uint64_t) { return Begin(commandId, compartment); }
  virtual Lookup Find(const std::string&, uint8_t, uint64_t) const { return Lookup::Missing; }
  // Epoch zero is legacy-only. Producers must retain the original envelope's
  // epoch on every retry; observing a new epoch never authorizes restamping it.
  virtual uint64_t CurrentEpoch() const { return 0; }
  virtual bool BeginScoped(uint64_t, const std::string&, uint8_t, uint64_t) { return false; }
  // Reservation persists only a pinned tombstone. Its one-use admission
  // capability belongs to this instance and must never survive a restart.
  virtual bool ReservePresentation(uint64_t, const std::string&, uint8_t, uint64_t) { return false; }
  virtual bool HasPresentationReservation() const { return false; }
  virtual bool HasPresentationReservation(uint64_t, const std::string&, uint8_t, uint64_t) const { return false; }
  virtual bool BeginReservedPresentation(uint64_t, const std::string&, uint8_t, uint64_t) { return false; }
  virtual void CancelPresentationReservation() {}
  virtual Lookup FindScoped(uint64_t, const std::string&, uint8_t, uint64_t) const {
    return Lookup::StaleEpoch;
  }
  // Historical evidence lookup does not authorize admission in the current epoch.
  virtual Lookup FindHistorical(uint64_t, const std::string&, uint8_t, uint64_t) const {
    return Lookup::Unavailable;
  }
  virtual bool ConfirmTimedPresentationScoped(uint64_t, const std::string&, uint8_t, uint64_t) { return false; }
  virtual bool WasTimedPresentationCompletedScoped(uint64_t, const std::string&, uint8_t, uint64_t) const { return false; }
  virtual bool WasTimedPresentationCompletedHistorical(uint64_t, const std::string&, uint8_t, uint64_t) const { return false; }
  // Persist the new availability mask and close the record atomically.
  virtual bool Resolve(uint8_t readyMask) = 0;
  // Close a presentation only after its prop is confirmed back at rest.
  // Legacy selection journals fail closed until they support this transition.
  virtual bool ResolveRestored(uint8_t) { return false; }
  // DV10 historical evidence: record only after autonomous completion and its
  // existing post-persist timing validation. Legacy admission/ready state is not proof.
  virtual bool ConfirmTimedPresentation(const std::string&, uint8_t, uint64_t) { return false; }
  virtual bool WasTimedPresentationCompleted(const std::string&, uint8_t) const { return false; }
  // Historical epoch-zero proof only; this never authorizes new admission.
  virtual bool WasLegacyTimedPresentationCompleted(const std::string& id, uint8_t slot) const {
    return WasTimedPresentationCompleted(id, slot);
  }
  // Persist a confirmed reload without opening or closing a selection record.
  virtual bool PersistReadyMask(uint8_t readyMask) = 0;
};

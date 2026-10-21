#pragma once

#include <stddef.h>
#include <stdint.h>

#include <type_traits>
#include <string>

#include "CommandJournal.h"

// A bounded, read-only publication of worker-owned journal state. This layout
// is an in-memory handoff only; it is not a storage or wire format.
struct JournalSnapshot {
  using Lookup = CommandJournal::Lookup;
  static constexpr size_t HistoryCapacity = 32;
  static constexpr size_t CommandIdCapacity = 37;
  static constexpr size_t DeviceIdCapacity = 65;

  enum class Origin : uint8_t { Missing, Legacy, Scoped, Unavailable };

  struct HistoryEntry {
    uint64_t epoch = 0;
    char id[CommandIdCapacity]{};
    uint8_t slot = 0;
    uint64_t fingerprint = 0;
    bool timedCompleted = false;
    bool evidencePinned = false;
  };

  struct Reservation {
    uint64_t epoch = 0;
    char id[CommandIdCapacity]{};
    uint8_t slot = 0;
    uint64_t fingerprint = 0;
  };

  bool healthy = false;
  bool storageMissing = false;
  Origin origin = Origin::Unavailable;
  uint64_t currentEpoch = 0;
  uint8_t readyMask = 0;
  uint8_t pendingSlot = 0;
  char pendingId[CommandIdCapacity]{};
  char deviceId[DeviceIdCapacity]{};
  size_t historyCount = 0;
  HistoryEntry history[HistoryCapacity]{};
  Reservation reservation{};

  bool HasPresentationReservation() const { return reservation.epoch != 0; }
  bool HasPresentationReservation(uint64_t epoch, const std::string& id,
                                 uint8_t slot, uint64_t fingerprint) const;
  bool CanBeginOperation() const;
  CommandJournal::Lookup Find(const std::string& id, uint8_t slot,
                              uint64_t fingerprint) const;
  CommandJournal::Lookup FindScoped(uint64_t epoch, const std::string& id,
                                   uint8_t slot, uint64_t fingerprint) const;
  CommandJournal::Lookup FindHistorical(uint64_t epoch, const std::string& id,
                                        uint8_t slot, uint64_t fingerprint) const;
  bool WasTimedPresentationCompleted(const std::string& id, uint8_t slot) const;
  bool WasLegacyTimedPresentationCompleted(const std::string& id, uint8_t slot) const;
  bool WasTimedPresentationCompletedScoped(uint64_t epoch, const std::string& id,
                                           uint8_t slot, uint64_t fingerprint) const;
  bool WasTimedPresentationCompletedHistorical(uint64_t epoch, const std::string& id,
                                               uint8_t slot, uint64_t fingerprint) const;
};

static_assert(std::is_trivially_copyable<JournalSnapshot>::value,
              "JournalSnapshot must be safe to copy between owners");

// CommandJournal-compatible query surface. All mutation methods intentionally
// fail closed: a snapshot carries observations, never admission authority.
class JournalSnapshotView final : public CommandJournal {
 public:
  explicit JournalSnapshotView(const JournalSnapshot& snapshot) : snapshot_(snapshot) {}

  bool HasPending() const override { return !snapshot_.healthy || snapshot_.pendingSlot != 0; }
  bool CanBeginOperation() const override { return snapshot_.CanBeginOperation(); }
  uint8_t PendingCompartment() const override { return snapshot_.healthy ? snapshot_.pendingSlot : 0; }
  uint8_t ReadyMask() const override { return snapshot_.healthy ? snapshot_.readyMask : 0; }
  uint64_t CurrentEpoch() const override { return snapshot_.healthy ? snapshot_.currentEpoch : 0; }
  Lookup Find(const std::string& id, uint8_t slot, uint64_t fingerprint) const override {
    return snapshot_.Find(id, slot, fingerprint);
  }
  Lookup FindScoped(uint64_t epoch, const std::string& id, uint8_t slot,
                    uint64_t fingerprint) const override {
    return snapshot_.FindScoped(epoch, id, slot, fingerprint);
  }
  Lookup FindHistorical(uint64_t epoch, const std::string& id, uint8_t slot,
                        uint64_t fingerprint) const override {
    return snapshot_.FindHistorical(epoch, id, slot, fingerprint);
  }
  bool HasPresentationReservation() const override { return snapshot_.healthy && snapshot_.HasPresentationReservation(); }
  bool HasPresentationReservation(uint64_t epoch, const std::string& id, uint8_t slot,
                                 uint64_t fingerprint) const override {
    return snapshot_.healthy && snapshot_.HasPresentationReservation(epoch, id, slot, fingerprint);
  }
  bool WasTimedPresentationCompleted(const std::string& id, uint8_t slot) const override {
    return snapshot_.WasTimedPresentationCompleted(id, slot);
  }
  bool WasLegacyTimedPresentationCompleted(const std::string& id, uint8_t slot) const override {
    return snapshot_.WasLegacyTimedPresentationCompleted(id, slot);
  }
  bool WasTimedPresentationCompletedScoped(uint64_t epoch, const std::string& id, uint8_t slot,
                                           uint64_t fingerprint) const override {
    return snapshot_.WasTimedPresentationCompletedScoped(epoch, id, slot, fingerprint);
  }
  bool WasTimedPresentationCompletedHistorical(uint64_t epoch, const std::string& id, uint8_t slot,
                                               uint64_t fingerprint) const override {
    return snapshot_.WasTimedPresentationCompletedHistorical(epoch, id, slot, fingerprint);
  }

  bool Begin(const std::string&, uint8_t) override { return false; }
  bool BeginWithIdentity(const std::string&, uint8_t, uint64_t) override { return false; }
  bool BeginScoped(uint64_t, const std::string&, uint8_t, uint64_t) override { return false; }
  bool ReservePresentation(uint64_t, const std::string&, uint8_t, uint64_t) override { return false; }
  bool BeginReservedPresentation(uint64_t, const std::string&, uint8_t, uint64_t) override { return false; }
  void CancelPresentationReservation() override {}
  bool ConfirmTimedPresentation(const std::string&, uint8_t, uint64_t) override { return false; }
  bool ConfirmTimedPresentationScoped(uint64_t, const std::string&, uint8_t, uint64_t) override { return false; }
  bool Resolve(uint8_t) override { return false; }
  bool ResolveRestored(uint8_t) override { return false; }
  bool PersistReadyMask(uint8_t) override { return false; }

 private:
  const JournalSnapshot& snapshot_;
};

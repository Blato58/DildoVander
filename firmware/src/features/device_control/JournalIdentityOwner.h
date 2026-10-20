#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <string>

#include "JournalEvidence.h"
#include "JournalStorage.h"
#include "PersistentCommandJournal.h"

struct JournalIdentityView {
  bool healthy = false;
  bool provisioned = false;
  std::string deviceId;
};

// Service-only identity preparation. Construction and validation never write or
// provision; the caller must keep normal admissions blocked until Ready.
class JournalIdentityOwner final {
 public:
  enum class Validation {
    Unestablished, Preparing, Ready, InspectionRequired, IdentityUnavailable,
    MarkerUnavailable, MissingJournal, IdentityMismatch, JournalUnavailable, MissingMarker
  };
  enum class Phase : uint8_t { Missing, PreparingFactory, PreparingLegacy, Established };
  static constexpr size_t MarkerHeaderSize = 80;
  static constexpr size_t EstablishedRecordSize = MarkerHeaderSize + 4;
  static constexpr size_t PreparingRecordSize = MarkerHeaderSize + PersistentCommandJournal::RecordSize + 4;
  static constexpr size_t MaximumRecordSize = PreparingRecordSize;

  JournalIdentityOwner(JournalStorage& markerStorage, PersistentCommandJournal& journal);
  bool IsHealthy() const;
  Phase CurrentPhase() const { return state_.phase; }
  const std::string& TargetDeviceId() const { return state_.deviceId; }
  Validation Validate(const JournalIdentityView& identity) const;
  bool BeginFactory(const std::string& target, const JournalIdentityView& identity, bool serviceAuthorized);
  bool BeginLegacy(const JournalIdentityView& identity, bool serviceAuthorized);
  bool ResumePreparation(const JournalIdentityView& identity, bool serviceAuthorized);
  bool Finalize(const JournalIdentityView& identity, const JournalEvidenceInventory& online,
                const JournalEvidenceInventory& offline, bool serviceAuthorized);

 private:
  struct State {
    Phase phase = Phase::Missing;
    std::string deviceId;
    uint64_t initialEpoch = 0;
    std::array<uint8_t, PersistentCommandJournal::RecordSize> expectedRecord{};
  };
  bool PreparationMatchesJournal() const;
  bool Persist(const State& next);
  static bool Decode(const uint8_t* bytes, size_t length, State& state);
  static size_t Encode(const State& state, std::array<uint8_t, MaximumRecordSize>& bytes);
  static bool ValidDeviceId(const std::string& deviceId);
  static bool ValidInventory(const JournalEvidenceInventory& inventory, const std::string& deviceId);

  JournalStorage& markerStorage_;
  PersistentCommandJournal& journal_;
  State state_;
  bool healthy_ = false;
};

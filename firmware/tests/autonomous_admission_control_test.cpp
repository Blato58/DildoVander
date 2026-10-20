#include <cassert>
#include <string>

#include "../src/features/device_control/DeviceControl.h"
#include "../src/features/safety/SafetyController.h"

namespace {
constexpr char Id[] = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
constexpr uint8_t Slot = 2;
constexpr uint64_t Fingerprint = 77;
using Progress = DeviceControl::AutonomousAdmissionProgress;

class Safety final : public SafetyPort {
 public:
  void DisableActuators() override { ++disables; }
  void StartVibration(uint8_t) override { ++effects; }
  void ReleaseLatch(uint8_t) override { ++effects; }
  unsigned effects = 0, disables = 0;
};

// ACK adoption and original-request consumption are separate, matching the
// asynchronous facade contract without requiring a storage worker in this test.
class DeferredJournal final : public CommandJournal {
 public:
  enum class Kind { None, Reserve, Begin };
  bool HasPending() const override { return open; }
  uint8_t PendingCompartment() const override { return open ? persistedSlot : 0; }
  uint8_t ReadyMask() const override { return readyMask; }
  uint64_t CurrentEpoch() const override { return epoch; }
  bool HasPendingStorage() const override { return kind != Kind::None; }
  StorageState LastStorageState() const override { return state; }
  bool HasStorageFault() const override { return storageFault; }
  bool Begin(const std::string& id, uint8_t slot) override {
    return BeginWithIdentity(id, slot, 0);
  }
  bool BeginWithIdentity(const std::string& id, uint8_t slot, uint64_t fingerprint) override {
    return Apply(Kind::Begin, 0, id, slot, fingerprint);
  }
  bool ReservePresentation(uint64_t operationEpoch, const std::string& id,
      uint8_t slot, uint64_t fingerprint) override {
    return Apply(Kind::Reserve, operationEpoch, id, slot, fingerprint);
  }
  bool BeginReservedPresentation(uint64_t operationEpoch, const std::string& id,
      uint8_t slot, uint64_t fingerprint) override {
    return Apply(Kind::Begin, operationEpoch, id, slot, fingerprint);
  }
  bool HasPresentationReservation() const override { return reserved; }
  bool HasPresentationReservation(uint64_t operationEpoch, const std::string& id,
      uint8_t slot, uint64_t fingerprint) const override {
    return reserved && SamePersisted(operationEpoch, id, slot, fingerprint);
  }
  void CancelPresentationReservation() override { reserved = false; }
  void CancelStorageAdmissions() override {
    ++cancellations;
    reserved = false;
    abandoned = HasPendingStorage();
    PollStorage();
  }
  void PollStorage() override {
    if (!acknowledged || kind == Kind::None) return;
    if (durable) {
      persistedEpoch = requestEpoch;
      persistedId = requestId;
      persistedSlot = requestSlot;
      persistedFingerprint = requestFingerprint;
      history = true;
      if (kind == Kind::Begin) { open = true; reserved = false; }
      else reserved = !abandoned;
    }
    if (abandoned) {
      kind = Kind::None;
      state = StorageState::Superseded;
      ++released;
    }
  }
  Lookup Find(const std::string& id, uint8_t slot, uint64_t fingerprint) const override {
    return FindScoped(0, id, slot, fingerprint);
  }
  Lookup FindScoped(uint64_t operationEpoch, const std::string& id,
      uint8_t slot, uint64_t fingerprint) const override {
    if (!history) return Lookup::Missing;
    return SamePersisted(operationEpoch, id, slot, fingerprint) ? Lookup::Same : Lookup::Conflict;
  }
  bool Resolve(uint8_t mask) override { readyMask = mask; open = false; return true; }
  bool PersistReadyMask(uint8_t mask) override { readyMask = mask; return true; }
  void Acknowledge(StorageState result = StorageState::Committed, bool writeCommitted = true) {
    assert(kind != Kind::None);
    acknowledged = true;
    ackResult = result;
    durable = writeCommitted;
    PollStorage();
  }
  bool Apply(Kind requestedKind, uint64_t operationEpoch, const std::string& id,
      uint8_t slot, uint64_t fingerprint) {
    ++calls;
    if (kind == Kind::None && requestedKind == Kind::Reserve && refuseReserve) {
      state = StorageState::Rejected;
      return false;
    }
    if (kind == Kind::None) {
      kind = requestedKind;
      requestEpoch = operationEpoch;
      requestId = id;
      requestSlot = slot;
      requestFingerprint = fingerprint;
      acknowledged = abandoned = durable = false;
      state = StorageState::Pending;
      return false;
    }
    assert(kind == requestedKind && requestEpoch == operationEpoch && requestId == id &&
        requestSlot == slot && requestFingerprint == fingerprint);
    PollStorage();
    if (!acknowledged) { state = StorageState::Pending; return false; }
    kind = Kind::None;
    state = ackResult;
    ++released;
    return state == StorageState::Committed;
  }
  bool SamePersisted(uint64_t operationEpoch, const std::string& id,
      uint8_t slot, uint64_t fingerprint) const {
    return persistedEpoch == operationEpoch && persistedId == id &&
        persistedSlot == slot && persistedFingerprint == fingerprint;
  }
  uint64_t epoch = 1, persistedEpoch = 0, requestEpoch = 0;
  uint64_t persistedFingerprint = 0, requestFingerprint = 0;
  uint8_t persistedSlot = 0, requestSlot = 0, readyMask = 63;
  std::string persistedId, requestId;
  Kind kind = Kind::None;
  StorageState state = StorageState::Rejected, ackResult = StorageState::Rejected;
  bool open = false, reserved = false, history = false, storageFault = false;
  bool acknowledged = false, abandoned = false, durable = false;
  bool refuseReserve = false;
  unsigned calls = 0, released = 0, cancellations = 0;
};

struct Fixture {
  explicit Fixture(uint64_t operationEpoch = 1, uint64_t calibration = 1)
      : safety(port), sequence(safety), control("device", "boot", sequence, journal, calibration) {
    journal.epoch = operationEpoch;
    safety.InitializeSafe();
    assert(control.RecoverAfterInspection());
    revision = control.Revision();
  }
  bool Reserve(uint64_t operationEpoch = 1) {
    return control.ReserveAutonomousPresentation(Id, Slot, revision, Fingerprint, operationEpoch);
  }
  bool Begin(uint64_t operationEpoch = 1) {
    return control.BeginAutonomousPresentation(Id, Slot, revision, Fingerprint, operationEpoch);
  }
  void FinishReserve() {
    assert(!Reserve() && control.LastAutonomousAdmissionProgress() == Progress::Pending);
    journal.Acknowledge();
    assert(Reserve() && control.LastAutonomousAdmissionProgress() == Progress::Succeeded);
  }
  Safety port;
  SafetyController safety;
  SelectionSequence sequence;
  DeferredJournal journal;
  DeviceControl control;
  uint64_t revision = 0;
};

void ExactReserveAndBegin() {
  Fixture fixture;
  assert(!fixture.Reserve());
  assert(fixture.control.LastAutonomousAdmissionProgress() == Progress::Pending);
  assert(fixture.control.HasStorageWork() && !fixture.control.HasAutonomousReservation());
  assert(!fixture.control.AcquireManualControl("lease", 0));
  assert(!fixture.Reserve() && fixture.port.effects == 0);
  const auto calls = fixture.journal.calls;
  assert(!fixture.control.ReserveAutonomousPresentation("different", Slot, fixture.revision, Fingerprint, 1));
  assert(!fixture.control.ReserveAutonomousPresentation(Id, Slot + 1, fixture.revision, Fingerprint, 1));
  assert(!fixture.control.ReserveAutonomousPresentation(Id, Slot, fixture.revision + 1, Fingerprint, 1));
  assert(!fixture.control.ReserveAutonomousPresentation(Id, Slot, fixture.revision, Fingerprint + 1, 1));
  assert(!fixture.control.ReserveAutonomousPresentation(Id, Slot, fixture.revision, Fingerprint, 2));
  assert(!fixture.Begin()); // A Begin cannot consume an owned Reserve result.
  assert(fixture.journal.calls == calls);
  assert(fixture.control.HasPendingAutonomousAdmission(Id, Slot, fixture.revision, Fingerprint, 1));
  fixture.journal.Acknowledge();
  assert(fixture.Reserve());
  assert(fixture.control.Revision() == fixture.revision && !fixture.control.HasStorageWork());
  assert(fixture.control.HasAutonomousReservation(Id, Slot, Fingerprint, 1));
  assert(!fixture.control.HasAutonomousPresentation() && fixture.port.effects == 0);

  assert(!fixture.Begin() && fixture.control.LastAutonomousAdmissionProgress() == Progress::Pending);
  assert(!fixture.Begin() && fixture.control.Revision() == fixture.revision);
  const auto beginCalls = fixture.journal.calls;
  assert(!fixture.control.BeginAutonomousPresentation("different", Slot, fixture.revision, Fingerprint, 1));
  assert(!fixture.control.BeginAutonomousPresentation(Id, Slot + 1, fixture.revision, Fingerprint, 1));
  assert(!fixture.control.BeginAutonomousPresentation(Id, Slot, fixture.revision, Fingerprint + 1, 1));
  assert(fixture.journal.calls == beginCalls);
  fixture.journal.Acknowledge(); // Adopt pending slot and consume volatile reservation before ACK.
  assert(fixture.journal.HasPending() && !fixture.journal.HasPresentationReservation());
  SelectionStep step;
  fixture.control.Tick(0, step);
  assert(fixture.control.Revision() == fixture.revision);
  assert(fixture.Begin() && fixture.control.LastAutonomousAdmissionProgress() == Progress::Succeeded);
  assert(fixture.control.HasAutonomousPresentation());
  assert(fixture.control.Revision() == fixture.revision + 1 && fixture.journal.released == 2);
  assert(fixture.port.effects == 0);
  assert(!fixture.control.ConfirmTimedPresentation());
}

void LegacyBeginAndFirstAdmissionGuards() {
  Fixture legacy(0);
  assert(!legacy.Reserve(0) && legacy.journal.calls == 0);
  assert(!legacy.Begin(0));
  assert(legacy.control.LastAutonomousAdmissionProgress() == Progress::Pending);
  legacy.journal.Acknowledge();
  assert(legacy.Begin(0));
  assert(legacy.control.HasAutonomousPresentation() && legacy.port.effects == 0);

  Fixture uncalibrated(1, 0);
  assert(!uncalibrated.Reserve() && uncalibrated.journal.calls == 0);
  Fixture missingReservation;
  assert(!missingReservation.Begin() && missingReservation.journal.calls == 0);
  Fixture stale;
  assert(!stale.control.ReserveAutonomousPresentation(Id, Slot, stale.revision - 1, Fingerprint, 1));
  assert(stale.journal.calls == 0);
  Fixture occupied;
  occupied.journal.kind = DeferredJournal::Kind::Begin;
  assert(!occupied.Reserve() && occupied.journal.calls == 0);
  Fixture refused;
  refused.journal.refuseReserve = true;
  assert(!refused.Reserve() && refused.control.LastAutonomousAdmissionProgress() == Progress::Rejected);
  assert(!refused.control.NeedsRecovery() && !refused.control.HasStorageWork());
  assert(refused.control.Revision() == refused.revision && refused.journal.cancellations == 0);
  refused.journal.refuseReserve = false;
  assert(!refused.Reserve() && refused.control.LastAutonomousAdmissionProgress() == Progress::Pending);
}

void OriginalStateAndFailedAck() {
  for (bool begin : {false, true}) {
    for (unsigned invalidation = 0; invalidation != 4; ++invalidation) {
      Fixture fixture;
      if (begin) fixture.FinishReserve();
      assert(!(begin ? fixture.Begin() : fixture.Reserve()));
      if (invalidation == 0) fixture.journal.epoch = 2;
      if (invalidation == 1) fixture.journal.readyMask &= static_cast<uint8_t>(~1u);
      if (invalidation == 2) { SelectionStep step; fixture.sequence.ReportFault(step); }
      if (invalidation == 3) fixture.journal.storageFault = true;
      assert(!(begin ? fixture.Begin() : fixture.Reserve()));
      assert(fixture.control.LastAutonomousAdmissionProgress() == Progress::Rejected);
      assert(fixture.control.NeedsRecovery() && fixture.port.effects == 0);
      fixture.journal.Acknowledge();
      fixture.journal.PollStorage();
      assert(!fixture.journal.HasPendingStorage());
      assert(!fixture.control.HasAutonomousPresentation());
    }
    for (auto result : {CommandJournal::StorageState::Rejected,
        CommandJournal::StorageState::Unknown, CommandJournal::StorageState::Superseded}) {
      Fixture fixture;
      if (begin) fixture.FinishReserve();
      assert(!(begin ? fixture.Begin() : fixture.Reserve()));
      fixture.journal.Acknowledge(result, true);
      assert(!(begin ? fixture.Begin() : fixture.Reserve()));
      assert(fixture.control.LastAutonomousAdmissionProgress() == Progress::Rejected);
      assert(fixture.control.NeedsRecovery() && !fixture.control.HasAutonomousPresentation());
      assert(!fixture.control.ConfirmTimedPresentation() && fixture.port.effects == 0);
    }
  }
}

void CancellationNeverResumes() {
  for (bool begin : {false, true}) {
    for (unsigned cancellation = 0; cancellation != 4; ++cancellation) {
      Fixture fixture;
      if (begin) fixture.FinishReserve();
      assert(!(begin ? fixture.Begin() : fixture.Reserve()));
      if (cancellation == 0) { SelectionStep step; fixture.control.Stop(step); }
      if (cancellation == 1) fixture.control.FaultAutonomousPresentation();
      if (cancellation == 2) fixture.control.CancelAutonomousReservation();
      if (cancellation == 3) assert(!fixture.control.RecoverAfterInspection());
      assert(fixture.control.LastAutonomousAdmissionProgress() == Progress::Rejected);
      fixture.journal.Acknowledge();
      fixture.journal.PollStorage();
      assert(!fixture.journal.HasPendingStorage());
      assert(!fixture.control.HasPendingAutonomousAdmission(Id, Slot, fixture.revision, Fingerprint, 1));
      assert(!(begin ? fixture.Begin() : fixture.Reserve()));
      assert(fixture.control.RecoverAfterInspection());
      assert(!(begin ? fixture.Begin() : fixture.Reserve())); // Original revision cannot be refreshed.
      assert(!fixture.control.HasAutonomousPresentation() && fixture.port.effects == 0);
      assert(!fixture.control.ConfirmTimedPresentation());
    }
  }
}
} // namespace

int main() {
  ExactReserveAndBegin();
  LegacyBeginAndFirstAdmissionGuards();
  OriginalStateAndFailedAck();
  CancellationNeverResumes();
}

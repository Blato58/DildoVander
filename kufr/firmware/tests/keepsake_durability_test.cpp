#include <algorithm>
#include <cassert>
#include <iostream>
#include <vector>
#include "../src/features/keepsake/KeepsakeOutbox.h"
#include "../src/features/device_control/PersistentCommandJournal.h"
#include "../src/features/device_control/DeviceControlRuntime.h"
#include "../src/features/wand_presentation/WandPresentation.h"
#include "../src/features/safety/SafetyController.h"

namespace {
constexpr char PhysicalId[] = "523ef590-ce4d-4901-91b3-79de1bfe7e35";

class Storage : public JournalStorage {
 public:
  JournalReadResult Read(uint8_t* output, size_t capacity, size_t& length) override {
    if (bytes.empty()) return JournalReadResult::Missing;
    if (bytes.size() > capacity) return JournalReadResult::Failed;
    std::copy(bytes.begin(), bytes.end(), output);
    length = bytes.size();
    return JournalReadResult::Found;
  }
  bool Write(const uint8_t* data, size_t length) override {
    if (failWrite) return false;
    bytes.assign(data, data + length);
    return !reportCommitFailure;
  }
  std::vector<uint8_t> bytes;
  bool failWrite = false;
  bool reportCommitFailure = false;
};

class InitializedStorage final : public Storage {
 public:
  InitializedStorage() {
    PersistentCommandJournal initial(*this);
    assert(initial.PersistReadyMask(63));
  }
};

class Safety final : public SafetyPort {
 public:
  void DisableActuators() override {}
  void StartVibration(uint8_t) override {}
  void ReleaseLatch(uint8_t) override {}
};

class Resting final : public WandPresentationPort {
 public:
  bool IsReady(uint8_t) const override { return true; }
  bool IsSafeToContinue() const override { return true; }
  bool IsRaised(uint8_t) const override { return true; }
  bool IsResting(uint8_t) const override { return true; }
  bool Raise(uint8_t, const std::string&, const ConversationRuntimeSnapshot&) override { return false; }
  bool Lower(uint8_t) override { return false; }
  bool Complete(uint8_t) override { return false; }
  void DisableAndFault() override {}
};

struct Fixture {
  InitializedStorage storage;
  PersistentCommandJournal journal{storage};
  Safety port;
  SafetyController safety{port};
  SelectionSequence sequence{safety};
  DeviceControl control{"kufr-001", "boot", sequence, journal, 1};
  ConversationRuntimeState snapshot{false, 0, "test"};
  DeviceControlRuntime runtime{control, sequence, snapshot, "test"};
  Resting resting;
  WandPresentation presentation{resting};
  Fixture() {
    assert(journal.PersistReadyMask(63));
    safety.InitializeSafe();
    assert(runtime.BindPresentation(presentation));
    assert(runtime.RecoverAfterInspection());
    assert(runtime.PublishCurrent());
  }
  void Begin() {
    ConversationRuntimeSnapshot expected;
    assert(snapshot.Read(expected));
    assert(runtime.BeginAutonomousPresentation(PhysicalId, 3, expected, 1234));
  }
};

KeepsakeOutcome Prepared() {
  KeepsakeOutcome item{"backend-operation", "exact-outcome", PhysicalId, PhysicalId, "original-boot", 9, 24, 0};
  item.compartment = 3;
  return item;
}

bool Timely(void*) { return true; }
bool TooLate(void*) { return false; }
bool FailMarker(void* context) { static_cast<Storage*>(context)->failWrite = true; return true; }
bool AmbiguousMarkerAck(void* context) { static_cast<Storage*>(context)->reportCommitFailure = true; return true; }

void RecoveryRequiresConfirmedPhysicalJournal() {
  Fixture fixture;
  fixture.Begin();
  KeepsakeOutbox before;
  assert(before.Prepare(Prepared()));
  const auto durablePreparation = before.State();
  assert(fixture.runtime.CompleteAutonomousPresentation(3, Timely));
  assert(fixture.journal.WasTimedPresentationCompleted(PhysicalId, 3));
  SelectionStep laterStop;
  fixture.runtime.Stop(laterStop);
  assert(fixture.journal.WasTimedPresentationCompleted(PhysicalId, 3));  // Later Stop does not erase historical fact.
  // Simulated reset after physical completion, before Queue/first completed-outbox persist.
  PersistentCommandJournal rebootedJournal(fixture.storage);
  KeepsakeOutbox rebooted;
  assert(rebooted.Restore({}, {}, durablePreparation.prepared));
  assert(rebooted.RecoverPrepared(rebootedJournal));
  const auto recovered = rebooted.Snapshot();
  assert(recovered.size() == 1 && recovered.front().outcomeId == "exact-outcome");
  assert(recovered.front().operationId == "backend-operation" && recovered.front().bootId == "original-boot");
  assert(recovered.front().generation == 9 && recovered.front().deviceRevision == 24);
  assert(recovered.front().visibilityEpoch == 0);
  assert(rebooted.State().prepared.empty());
  auto wrongSlot = Prepared();
  wrongSlot.compartment = 4;
  KeepsakeOutbox mismatched;
  assert(mismatched.Restore({}, {}, {wrongSlot}));
  assert(mismatched.RecoverPrepared(rebootedJournal) && mismatched.Snapshot().empty());
}

void CommittedMarkerDespiteAckFailureIsHistoricalEvidence() {
  Fixture fixture;
  fixture.Begin();
  assert(fixture.runtime.CompleteAutonomousPresentation(3, AmbiguousMarkerAck, &fixture.storage));
  // Current owner suppresses DV10 on uncertain storage acknowledgement.
  assert(!fixture.journal.IsHealthy() && !fixture.journal.WasTimedPresentationCompleted(PhysicalId, 3));
  // Reboot reads valid committed bytes after the exact already-validated timed completion.
  PersistentCommandJournal rebootedJournal(fixture.storage);
  assert(rebootedJournal.IsHealthy() && rebootedJournal.WasTimedPresentationCompleted(PhysicalId, 3));
  KeepsakeOutbox outbox;
  assert(outbox.Restore({}, {}, {Prepared()}));
  assert(outbox.RecoverPrepared(rebootedJournal));
  assert(outbox.Snapshot().size() == 1 && outbox.Snapshot().front().outcomeId == "exact-outcome");
  assert(outbox.Snapshot().front().visibilityEpoch == 0);
  assert(outbox.RecoverPrepared(rebootedJournal) && outbox.Snapshot().size() == 1);
}

void LateFailedStoppedAndLegacyNeverRecover() {
  for (int scenario = 0; scenario < 3; ++scenario) {
    Fixture fixture;
    fixture.Begin();
    if (scenario == 0) assert(!fixture.runtime.CompleteAutonomousPresentation(3, TooLate));
    if (scenario == 1) assert(fixture.runtime.CompleteAutonomousPresentation(3, FailMarker, &fixture.storage));
    if (scenario == 2) { SelectionStep step; fixture.runtime.Stop(step); }
    assert(!fixture.journal.WasTimedPresentationCompleted(PhysicalId, 3));
    PersistentCommandJournal restarted(fixture.storage);
    KeepsakeOutbox outbox;
    assert(outbox.Restore({}, {}, {Prepared()}));
    assert(outbox.RecoverPrepared(restarted));
    assert(outbox.Snapshot().empty());
    assert(outbox.State().quarantined.front().reason == "physical_outcome_unknown");
  }
  Storage legacy;
  PersistentCommandJournal journal(legacy);
  assert(journal.PersistReadyMask(63));
  assert(journal.BeginWithIdentity(PhysicalId, 3, 1234));
  assert(journal.ResolveRestored(3));
  legacy.bytes[4] = 2;
  PersistentCommandJournal old(legacy);
  assert(old.IsHealthy() && !old.WasTimedPresentationCompleted(PhysicalId, 3));
  KeepsakeOutbox outbox;
  assert(outbox.Restore({}, {}, {Prepared()}));
  assert(outbox.RecoverPrepared(old) && outbox.Snapshot().empty());
  Storage manual;
  PersistentCommandJournal selection(manual);
  assert(selection.PersistReadyMask(63) && selection.Begin(PhysicalId, 3) && selection.ResolveRestored(3));
  assert(!selection.ConfirmTimedPresentation(PhysicalId, 3, 0));
}
}  // namespace

int main() {
  RecoveryRequiresConfirmedPhysicalJournal();
  CommittedMarkerDespiteAckFailureIsHistoricalEvidence();
  LateFailedStoppedAndLegacyNeverRecover();
  std::cout << "Prepared keepsake recovery across physical-completion reset and rejection gates passed\n";
}

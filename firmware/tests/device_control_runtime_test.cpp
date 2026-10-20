#include <assert.h>
#include <stdint.h>

#include <string>

#include "../src/features/device_control/DeviceControlRuntime.h"
#include "../src/features/safety/SafetyController.h"

namespace {

class FakeSafetyPort final : public SafetyPort {
 public:
  void DisableActuators() override {}
  void StartVibration(uint8_t) override {}
  void ReleaseLatch(uint8_t) override {}
};

class FakeJournal final : public CommandJournal {
 public:
  bool HasPending() const override { return pending || fault; }
  uint8_t PendingCompartment() const override { return compartment; }
  uint8_t ReadyMask() const override { return readyMask; }

  bool Begin(const std::string&, uint8_t selected) override {
    if (failBegin) {
      fault = true;
      return false;
    }
    pending = true;
    compartment = selected;
    return true;
  }

  bool Resolve(uint8_t nextMask) override {
    if (failResolve) {
      fault = true;
      return false;
    }
    readyMask = nextMask;
    pending = false;
    compartment = 0;
    return true;
  }

  bool PersistReadyMask(uint8_t nextMask) override {
    if (failReload) {
      fault = true;
      return false;
    }
    readyMask = nextMask;
    return true;
  }

  uint8_t readyMask = 63;
  uint8_t compartment = 0;
  bool pending = false;
  bool fault = false;
  bool failBegin = false;
  bool failResolve = false;
  bool failReload = false;
};

struct Fixture {
  Fixture()
      : safety(port),
        sequence(safety),
        control("kufr-1", "boot-1", sequence, journal, 1),
        state(false, 0, ""),
        runtime(control, sequence, state, "neutral-1") {
    safety.InitializeSafe();
  }

  SelectionCommand Selection(uint64_t revision) const {
    SelectionCommand command;
    command.commandId = "selection-1";
    command.deviceId = "kufr-1";
    command.bootId = "boot-1";
    command.controlLeaseId = "lease-1";
    command.sequence = 1;
    command.expectedRevision = revision;
    command.issuedAtDeviceMs = 0;
    command.maxAgeMs = 2000;
    command.compartment = 1;
    return command;
  }

  InventoryReloadCommand Reload(uint64_t revision) const {
    InventoryReloadCommand command;
    command.commandId = "reload-1";
    command.deviceId = "kufr-1";
    command.bootId = "boot-1";
    command.controlLeaseId = "lease-1";
    command.sequence = 2;
    command.expectedRevision = revision;
    command.issuedAtDeviceMs = 3000;
    command.maxAgeMs = 2000;
    command.compartment = 1;
    command.inspectionConfirmed = true;
    return command;
  }

  FakeSafetyPort port;
  SafetyController safety;
  SelectionSequence sequence;
  FakeJournal journal;
  DeviceControl control;
  ConversationRuntimeState state;
  DeviceControlRuntime runtime;
};

void TestDurableTransitions() {
  Fixture fixture;
  ConversationRuntimeSnapshot initial;
  assert(!fixture.runtime.PublishCurrent());
  assert(!fixture.state.Read(initial));
  assert(fixture.runtime.RecoverAfterInspection());
  assert(fixture.state.Read(initial));
  assert(initial.deviceRevision == 1 && initial.readyMask == 63);
  assert(fixture.runtime.AcquireManualControl("lease-1", 0));
  ConversationRuntimeSnapshot manual;
  assert(fixture.state.Read(manual));
  assert(manual.deviceRevision == 2 && manual.version > initial.version);
  assert(fixture.runtime.RenewManualLease("lease-1", 100));
  ConversationRuntimeSnapshot renewed;
  assert(fixture.state.Read(renewed));
  assert(renewed.version == manual.version);

  assert(fixture.runtime.AcceptSelection(fixture.Selection(manual.deviceRevision),
                                         true, 100).outcome ==
         CommandOutcome::Accepted);
  assert(fixture.journal.pending);
  assert(!fixture.state.Read(renewed));
  assert(!fixture.state.Matches(manual));
  SelectionStep step;
  assert(fixture.runtime.BeginAcceptedSelection(100, step));
  for (uint64_t nowMs = 110; nowMs < 5000 && fixture.journal.pending;
       nowMs += 10) {
    fixture.runtime.Tick(nowMs, step);
  }
  assert(!fixture.journal.pending);
  ConversationRuntimeSnapshot selected;
  assert(fixture.state.Read(selected));
  assert(selected.deviceRevision > manual.deviceRevision);
  assert(selected.readyMask == 62 && fixture.journal.readyMask == 62);

  assert(fixture.runtime.ConfirmReload(fixture.Reload(selected.deviceRevision),
                                       true, 4000).outcome ==
         CommandOutcome::Accepted);
  ConversationRuntimeSnapshot reloaded;
  assert(fixture.state.Read(reloaded));
  assert(reloaded.readyMask == 63 && fixture.journal.readyMask == 63);
  assert(reloaded.deviceRevision > selected.deviceRevision);
  assert(!fixture.state.Matches(selected));
}

void TestFailureAndLeaseLoss() {
  Fixture fixture;
  assert(fixture.runtime.RecoverAfterInspection());
  assert(fixture.runtime.AcquireManualControl("lease-1", 0));
  ConversationRuntimeSnapshot before;
  assert(fixture.state.Read(before));
  fixture.journal.failBegin = true;
  assert(fixture.runtime.AcceptSelection(fixture.Selection(before.deviceRevision),
                                         true, 100).error ==
         CommandError::JournalFailed);
  assert(!fixture.state.Read(before));

  Fixture leaseFixture;
  assert(leaseFixture.runtime.RecoverAfterInspection());
  assert(leaseFixture.runtime.AcquireManualControl("lease-1", 0));
  assert(leaseFixture.state.Read(before));
  assert(!leaseFixture.runtime.RenewManualLease("lease-1", 6000));
  assert(leaseFixture.runtime.Generation() == 1);
  assert(!leaseFixture.state.Read(before));

  Fixture idleLeaseFixture;
  assert(idleLeaseFixture.runtime.RecoverAfterInspection());
  assert(idleLeaseFixture.runtime.AcquireManualControl("lease-1", 0));
  assert(idleLeaseFixture.state.Read(before));
  SelectionStep idleLeaseStep;
  idleLeaseFixture.runtime.Tick(6000, idleLeaseStep);
  assert(!idleLeaseFixture.control.HasManualControl());
  assert(idleLeaseFixture.runtime.Generation() == 1);
  assert(!idleLeaseFixture.state.Read(before));

  Fixture prestartFixture;
  assert(prestartFixture.runtime.RecoverAfterInspection());
  assert(prestartFixture.runtime.AcquireManualControl("lease-1", 0));
  SelectionStep prestartStep;
  assert(!prestartFixture.runtime.Tick(0, prestartStep));
  assert(prestartFixture.state.Read(before));
  assert(prestartFixture.runtime.AcceptSelection(
             prestartFixture.Selection(before.deviceRevision), true, 100)
             .outcome == CommandOutcome::Accepted);
  assert(prestartFixture.runtime.Tick(300, prestartStep));
  assert(prestartStep.effect == SelectionEffect::Stopped);
  assert(prestartFixture.journal.pending);
  assert(!prestartFixture.state.Read(before));

  Fixture stopFixture;
  assert(stopFixture.runtime.RecoverAfterInspection());
  assert(stopFixture.state.Read(before));
  SelectionStep step;
  assert(stopFixture.runtime.Stop(step));
  assert(stopFixture.runtime.Generation() == 1);
  assert(!stopFixture.state.Read(before));
}

}  // namespace

int main() {
  {
    Fixture fixture;
    assert(fixture.runtime.RecoverAfterInspection());
    ConversationRuntimeSnapshot before;
    assert(fixture.state.Read(before));
    fixture.runtime.SetExternalActivity(true);
    assert(!fixture.state.Matches(before));
    assert(!fixture.runtime.AcquireManualControl("lease-1", 0));
    assert(!fixture.runtime.RecoverAfterInspection());
    assert(fixture.runtime.AcceptSelection(fixture.Selection(before.deviceRevision), true, 0).error == CommandError::Busy);
    fixture.runtime.SetExternalActivity(false);
    ConversationRuntimeSnapshot after;
    assert(fixture.state.Read(after) && after.generation == before.generation + 1);
    assert(!fixture.state.Matches(before));
  }
  TestDurableTransitions();
  TestFailureAndLeaseLoss();
}

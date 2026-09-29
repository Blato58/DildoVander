#include <assert.h>
#include <stdint.h>

#include <string>

#include "../src/features/device_control/DeviceControl.h"
#include "../src/features/safety/SafetyController.h"

namespace {

class FakeSafetyPort : public SafetyPort {
 public:
  void DisableActuators() override {
    vibrationOn = false;
    latchOn = false;
  }

  void StartVibration(uint8_t) override {
    assert(!vibrationOn && !latchOn);
    vibrationOn = true;
  }

  void ReleaseLatch(uint8_t) override {
    assert(!vibrationOn && !latchOn);
    latchOn = true;
    ++releaseCount;
  }

  bool vibrationOn = false;
  bool latchOn = false;
  int releaseCount = 0;
};

class FakeJournal : public CommandJournal {
 public:
  explicit FakeJournal(uint8_t readyMask = 63)
      : persistedReadyMask(readyMask) {}

  bool HasPending() const override { return pending; }
  uint8_t PendingCompartment() const override { return compartment; }
  uint8_t ReadyMask() const override { return persistedReadyMask; }

  bool Begin(const std::string& commandId, uint8_t selected) override {
    ++beginCount;
    if (failBegin || pending) {
      return false;
    }

    pending = true;
    compartment = selected;
    lastCommandId = commandId;
    return true;
  }

  bool Resolve(uint8_t readyMask) override {
    ++resolveCount;
    if (failResolve) {
      return false;
    }

    persistedReadyMask = readyMask;
    pending = false;
    compartment = 0;
    return true;
  }

  bool pending = false;
  bool failBegin = false;
  bool failResolve = false;
  uint8_t persistedReadyMask = 63;
  uint8_t compartment = 0;
  int beginCount = 0;
  int resolveCount = 0;
  std::string lastCommandId;
};

struct Fixture {
  explicit Fixture(uint8_t readyMask = 63)
      : safety(port),
        sequence(safety),
        journal(readyMask),
        control("kufr-001", "boot-1", sequence, journal, 1) {
    safety.InitializeSafe();
    assert(control.RecoverAfterInspection());
    assert(control.AcquireManualControl("lease-1", 0));
  }

  SelectionCommand Command(uint64_t sequenceNumber, uint8_t compartment,
                           const std::string& commandId,
                           uint64_t issuedAtMs = 0) const {
    SelectionCommand command;
    command.commandId = commandId;
    command.deviceId = "kufr-001";
    command.bootId = "boot-1";
    command.controlLeaseId = "lease-1";
    command.sequence = sequenceNumber;
    command.expectedRevision = control.Revision();
    command.issuedAtDeviceMs = issuedAtMs;
    command.maxAgeMs = 2000;
    command.compartment = compartment;
    return command;
  }

  FakeSafetyPort port;
  SafetyController safety;
  SelectionSequence sequence;
  FakeJournal journal;
  DeviceControl control;
};

void Advance(Fixture& fixture, uint32_t startMs, uint32_t endMs) {
  for (uint32_t nowMs = startMs; nowMs <= endMs; nowMs += 50) {
    SelectionStep step;
    fixture.control.Tick(nowMs, step);
  }
}

void TestLostAckAndDuplicateCompletion() {
  Fixture fixture;
  const SelectionCommand command = fixture.Command(1, 4, "command-1");

  CommandReceipt receipt = fixture.control.AcceptSelection(command, true, 0);
  assert(receipt.error == CommandError::None);
  assert(receipt.outcome == CommandOutcome::Accepted);
  assert(!fixture.sequence.IsBusy());
  assert(fixture.journal.pending);
  assert(fixture.journal.beginCount == 1);

  for (uint64_t retryMs : {500u, 1000u}) {
    receipt = fixture.control.AcceptSelection(command, true, retryMs);
    assert(receipt.duplicate);
    assert(receipt.outcome == CommandOutcome::Accepted);
    assert(fixture.journal.beginCount == 1);
  }

  SelectionStep step;
  assert(fixture.control.BeginAcceptedSelection(1000, step));
  assert(step.effect == SelectionEffect::SearchStep);
  Advance(fixture, 1050, 3300);
  assert(!fixture.sequence.IsBusy());
  assert(!fixture.journal.pending);
  assert(fixture.journal.resolveCount == 1);
  assert(fixture.journal.persistedReadyMask == 55);
  assert(fixture.port.releaseCount == 1);
  assert(fixture.control.ReadyMask() == 55);

  receipt = fixture.control.AcceptSelection(command, true, 3400);
  assert(receipt.error == CommandError::Expired);
  assert(fixture.journal.beginCount == 1);
  assert(fixture.port.releaseCount == 1);

  FakeSafetyPort rebootPort;
  SafetyController rebootSafety(rebootPort);
  SelectionSequence rebootSequence(rebootSafety);
  DeviceControl rebooted("kufr-001", "boot-2", rebootSequence,
                         fixture.journal, 1);
  assert(rebooted.ReadyMask() == 55);
}

void TestIdConflictAndSequenceRules() {
  Fixture fixture;
  const SelectionCommand first = fixture.Command(1, 2, "command-1");
  assert(fixture.control.AcceptSelection(first, true, 0).outcome ==
         CommandOutcome::Accepted);

  SelectionCommand changed = first;
  changed.compartment = 3;
  assert(fixture.control.AcceptSelection(changed, true, 0).error ==
         CommandError::IdConflict);

  SelectionCommand gap = fixture.Command(3, 3, "command-3");
  assert(fixture.control.AcceptSelection(gap, true, 0).error ==
         CommandError::SequenceGap);

  SelectionCommand busy = fixture.Command(2, 3, "command-2");
  assert(fixture.control.AcceptSelection(busy, true, 0).error ==
         CommandError::Busy);
  assert(fixture.control.AcceptSelection(busy, true, 0).duplicate);

  SelectionStep step;
  assert(fixture.control.Stop(step));
  assert(fixture.journal.pending);
  assert(fixture.control.NeedsRecovery());
  SelectionCommand afterStop = fixture.Command(3, 3, "command-3");
  assert(fixture.control.AcceptSelection(afterStop, true, 0).error ==
         CommandError::SafetyLatched);
  assert(fixture.port.releaseCount == 0);
}

void TestJournalFailureAndReboot() {
  Fixture fixture;
  fixture.journal.failBegin = true;
  const SelectionCommand failed = fixture.Command(1, 1, "journal-failed");
  assert(fixture.control.AcceptSelection(failed, true, 0).error ==
         CommandError::JournalFailed);
  assert(!fixture.control.HasPendingSelection());
  assert(!fixture.sequence.IsBusy());
  assert(fixture.port.releaseCount == 0);

  fixture.journal.failBegin = false;
  const SelectionCommand accepted = fixture.Command(2, 2, "pending-on-reboot");
  assert(fixture.control.AcceptSelection(accepted, true, 0).outcome ==
         CommandOutcome::Accepted);
  assert(fixture.journal.pending);

  FakeSafetyPort rebootPort;
  SafetyController rebootSafety(rebootPort);
  SelectionSequence rebootSequence(rebootSafety);
  DeviceControl rebooted("kufr-001", "boot-2", rebootSequence,
                         fixture.journal, 1);
  rebootSafety.InitializeSafe();
  assert(rebooted.NeedsRecovery());
  assert(!rebooted.AcquireManualControl("lease-2", 0));
  assert(rebooted.RecoverAfterInspection());
  assert(!fixture.journal.pending);
  assert(rebooted.ReadyMask() == 61);
  assert(rebooted.AcquireManualControl("lease-2", 0));

  SelectionCommand oldBoot = accepted;
  oldBoot.controlLeaseId = "lease-2";
  assert(rebooted.AcceptSelection(oldBoot, true, 0).error ==
         CommandError::WrongBoot);
  assert(rebootPort.releaseCount == 0);
}

void TestCacheEvictionAndMalformedEnvelope() {
  Fixture fixture(0);
  SelectionCommand malformed = fixture.Command(1, 0, "malformed");
  assert(fixture.control.AcceptSelection(malformed, true, 0).error ==
         CommandError::InvalidMessage);

  for (uint64_t number = 1; number <= 65; ++number) {
    const SelectionCommand command =
        fixture.Command(number, 1, "unavailable-" + std::to_string(number));
    assert(fixture.control.AcceptSelection(command, true, 0).error ==
           CommandError::CompartmentUnavailable);
  }

  const SelectionCommand evicted = fixture.Command(1, 1, "unavailable-1");
  assert(fixture.control.AcceptSelection(evicted, true, 0).error ==
         CommandError::AlreadySeenResultUnavailable);
  const SelectionCommand cached = fixture.Command(65, 1, "unavailable-65");
  assert(fixture.control.AcceptSelection(cached, true, 0).duplicate);
  assert(fixture.journal.beginCount == 0);
}

void TestAuthorizationAndLateCommand() {
  Fixture fixture;
  SelectionCommand command = fixture.Command(1, 1, "late-command");
  assert(fixture.control.AcceptSelection(command, false, 0).error ==
         CommandError::Unauthorized);
  assert(fixture.control.AcceptSelection(command, true, 2001).error ==
         CommandError::Expired);
  assert(fixture.journal.beginCount == 0);
  assert(fixture.port.releaseCount == 0);

  command = fixture.Command(2, 2, "wrong-lease", 2001);
  command.controlLeaseId = "another-lease";
  assert(fixture.control.AcceptSelection(command, true, 2001).error ==
         CommandError::LeaseExpired);

  command = fixture.Command(2, 2, "fresh-command", 2001);
  assert(fixture.control.AcceptSelection(command, true, 2001).outcome ==
         CommandOutcome::Accepted);
  assert(fixture.journal.beginCount == 1);
  assert(fixture.port.releaseCount == 0);
}

void TestLeaseLossStopsActiveOutput() {
  Fixture fixture;
  const SelectionCommand command = fixture.Command(1, 1, "lease-loss");
  assert(fixture.control.AcceptSelection(command, true, 0).outcome ==
         CommandOutcome::Accepted);
  SelectionStep step;
  assert(fixture.control.BeginAcceptedSelection(0, step));
  Advance(fixture, 50, 1000);
  assert(fixture.port.vibrationOn);

  assert(fixture.control.Tick(6000, step));
  assert(step.effect == SelectionEffect::Stopped);
  assert(!fixture.port.vibrationOn);
  assert(fixture.control.NeedsRecovery());
  assert(fixture.journal.pending);
  assert(!fixture.control.AcquireManualControl("new-lease", 6000));
  assert(fixture.port.releaseCount == 0);
}

void TestCommandIdCannotCrossManualLeases() {
  Fixture fixture;
  const SelectionCommand original = fixture.Command(1, 1, "shared-command-id");
  assert(fixture.control.AcceptSelection(original, true, 0).outcome ==
         CommandOutcome::Accepted);

  SelectionStep step;
  assert(fixture.control.Stop(step));
  assert(fixture.control.DisconnectManualControl(step) == false);
  assert(fixture.control.RecoverAfterInspection());
  assert(fixture.control.AcquireManualControl("lease-2", 100));

  SelectionCommand reused = fixture.Command(1, 2, "shared-command-id", 100);
  reused.controlLeaseId = "lease-2";
  assert(fixture.control.AcceptSelection(reused, true, 100).error ==
         CommandError::IdConflict);
  assert(fixture.journal.beginCount == 1);
  assert(fixture.port.releaseCount == 0);
}

void TestRebootAfterInterruptedRelease() {
  Fixture fixture;
  const SelectionCommand command = fixture.Command(1, 4, "release-aborted");
  assert(fixture.control.AcceptSelection(command, true, 0).outcome ==
         CommandOutcome::Accepted);
  SelectionStep step;
  assert(fixture.control.BeginAcceptedSelection(0, step));
  Advance(fixture, 50, 1900);
  assert(fixture.port.latchOn);
  assert(fixture.control.Stop(step));
  assert(!fixture.port.latchOn);
  assert(fixture.journal.pending);
  assert(fixture.journal.PendingCompartment() == 4);

  FakeSafetyPort rebootPort;
  SafetyController rebootSafety(rebootPort);
  SelectionSequence rebootSequence(rebootSafety);
  DeviceControl rebooted("kufr-001", "boot-2", rebootSequence,
                         fixture.journal, 1);
  rebootSafety.InitializeSafe();
  assert(rebooted.NeedsRecovery());
  assert(!rebooted.AcquireManualControl("lease-2", 0));
  assert(rebooted.RecoverAfterInspection());
  assert(rebooted.ReadyMask() == 55);
  assert(!fixture.journal.pending);
  assert(rebootPort.releaseCount == 0);
}

void TestJournalResolveFailureDoesNotReportCompletion() {
  Fixture fixture;
  fixture.journal.failResolve = true;
  const SelectionCommand command = fixture.Command(1, 4, "resolve-failed");
  assert(fixture.control.AcceptSelection(command, true, 0).outcome ==
         CommandOutcome::Accepted);
  SelectionStep step;
  assert(fixture.control.BeginAcceptedSelection(0, step));

  for (uint32_t nowMs = 50; nowMs <= 2300; nowMs += 50) {
    fixture.control.Tick(nowMs, step);
  }

  assert(step.effect == SelectionEffect::Stopped);
  assert(step.compartment == 4);
  assert(fixture.safety.IsFaulted());
  assert(fixture.journal.pending);
  assert(fixture.control.NeedsRecovery());
}

}  // namespace

int main() {
  TestLostAckAndDuplicateCompletion();
  TestIdConflictAndSequenceRules();
  TestJournalFailureAndReboot();
  TestCacheEvictionAndMalformedEnvelope();
  TestAuthorizationAndLateCommand();
  TestLeaseLossStopsActiveOutput();
  TestCommandIdCannotCrossManualLeases();
  TestRebootAfterInterruptedRelease();
  TestJournalResolveFailureDoesNotReportCompletion();
}

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

// This fake separates snapshot adoption from consuming a request's ACK, like
// AsyncCommandJournal. Stop abandons ownership but still adopts durable history.
class CompletionJournal final : public CommandJournal {
 public:
  bool HasPending() const override { return open; }
  uint8_t PendingCompartment() const override { return open ? slot : 0; }
  uint8_t ReadyMask() const override { return 63; }
  uint64_t CurrentEpoch() const override { return 9; }
  bool Begin(const std::string&, uint8_t) override { return false; }
  bool Resolve(uint8_t) override { return true; }
  bool PersistReadyMask(uint8_t) override { return true; }
  bool ReservePresentation(uint64_t epoch, const std::string& id,
      uint8_t selected, uint64_t fingerprint) override {
    operationEpoch = epoch;
    operationId = id;
    slot = selected;
    operationFingerprint = fingerprint;
    reserved = true;
    return true;
  }
  bool HasPresentationReservation(uint64_t epoch, const std::string& id,
      uint8_t selected, uint64_t fingerprint) const override {
    return reserved && Same(epoch, id, selected, fingerprint);
  }
  bool BeginReservedPresentation(uint64_t epoch, const std::string& id,
      uint8_t selected, uint64_t fingerprint) override {
    assert(HasPresentationReservation(epoch, id, selected, fingerprint));
    reserved = false;
    open = true;
    return true;
  }
  Lookup FindScoped(uint64_t epoch, const std::string& id,
      uint8_t selected, uint64_t fingerprint) const override {
    return Same(epoch, id, selected, fingerprint) ? Lookup::Same : Lookup::Conflict;
  }
  bool ResolveRestored(uint8_t selected) override {
    assert(selected == slot && !proofPending);
    ++closeCalls;
    if (synchronous) { open = false; state = StorageState::Committed; return true; }
    if (!closePending) {
      assert(open);
      closePending = true;
      state = StorageState::Pending;
      return false;
    }
    PollStorage();
    if (!closeAck) { state = StorageState::Pending; return false; }
    closePending = false;
    ++released;
    state = failClose ? StorageState::Unknown : StorageState::Committed;
    return !failClose;
  }
  bool ConfirmTimedPresentationScoped(uint64_t epoch, const std::string& id,
      uint8_t selected, uint64_t fingerprint) override {
    assert(Same(epoch, id, selected, fingerprint));
    assert(!open && !closePending);
    ++proofCalls;
    if (synchronous) { proven = true; state = StorageState::Committed; return true; }
    if (!proofPending) {
      proofPending = true;
      state = StorageState::Pending;
      return false;
    }
    PollStorage();
    if (!proofAck) { state = StorageState::Pending; return false; }
    proofPending = false;
    ++released;
    state = failProof ? StorageState::Unknown : StorageState::Committed;
    return !failProof;
  }
  StorageState LastStorageState() const override { return state; }
  bool HasPendingStorage() const override { return closePending || proofPending; }
  void CancelStorageAdmissions() override { abandoned = true; reserved = false; }
  void PollStorage() override {
    if (closePending && closeAck && !failClose) open = false;
    if (proofPending && proofAck && !failProof) proven = true;
    if (abandoned && ((closePending && closeAck) || (proofPending && proofAck))) {
      closePending = proofPending = false;
      ++released;
      state = StorageState::Superseded;
    }
  }
  bool Same(uint64_t epoch, const std::string& id,
      uint8_t selected, uint64_t fingerprint) const {
    return epoch == operationEpoch && id == operationId && selected == slot &&
        fingerprint == operationFingerprint;
  }
  uint64_t operationEpoch = 0, operationFingerprint = 0;
  std::string operationId;
  uint8_t slot = 0;
  bool open = false, reserved = false;
  bool closePending = false, proofPending = false;
  bool closeAck = false, proofAck = false, proven = false;
  bool failClose = false, failProof = false, abandoned = false;
  bool synchronous = false;
  unsigned closeCalls = 0, proofCalls = 0, released = 0;
  StorageState state = StorageState::Rejected;
};

struct CompletionFixture {
  CompletionFixture() : safety(port), sequence(safety),
      control("kufr-1", "boot-1", sequence, journal, 1),
      state(false, 0, ""), runtime(control, sequence, state, "neutral-1") {
    safety.InitializeSafe();
    assert(runtime.RecoverAfterInspection());
    const uint64_t revision = control.Revision();
    assert(control.ReserveAutonomousPresentation("original-operation", 2, revision, 123, 9));
    assert(control.BeginAutonomousPresentation("original-operation", 2, revision, 123, 9));
    runtime.PublishCurrent();
  }
  static bool Check(void* context) {
    auto& fixture = *static_cast<CompletionFixture*>(context);
    assert(!fixture.journal.open && !fixture.journal.closePending);
    ++fixture.checkCalls;
    if (fixture.stopInCheck) {
      SelectionStep step;
      fixture.runtime.Stop(step);
    }
    return fixture.timely;
  }
  static bool OtherCheck(void*) { return true; }
  using Progress = DeviceControlRuntime::PresentationProgress;
  Progress Advance() { return runtime.AdvanceAutonomousCompletion(2, Check, this); }
  FakeSafetyPort port;
  SafetyController safety;
  SelectionSequence sequence;
  CompletionJournal journal;
  DeviceControl control;
  ConversationRuntimeState state;
  DeviceControlRuntime runtime;
  unsigned checkCalls = 0;
  bool timely = true, stopInCheck = false;
};

void TestAsyncCompletionSeal() {
  using Progress = CompletionFixture::Progress;
  CompletionFixture fixture;
  assert(fixture.Advance() == Progress::Pending);
  assert(fixture.Advance() == Progress::Pending);
  assert(fixture.control.HasAutonomousPresentation());
  assert(!fixture.runtime.HasValidatedAutonomousCompletion());
  assert(fixture.checkCalls == 0 && fixture.journal.proofCalls == 0);
  assert(fixture.runtime.AdvanceAutonomousCompletion(3, CompletionFixture::Check,
      &fixture) == Progress::Rejected);
  assert(fixture.runtime.AdvanceAutonomousCompletion(2, CompletionFixture::OtherCheck,
      &fixture) == Progress::Rejected);
  int otherContext = 0;
  assert(fixture.runtime.AdvanceAutonomousCompletion(2, CompletionFixture::Check,
      &otherContext) == Progress::Rejected);
  // The facade may publish the closed snapshot before its caller consumes ACK.
  fixture.journal.closeAck = true;
  fixture.journal.PollStorage();
  assert(!fixture.journal.HasPending());
  assert(fixture.Advance() == Progress::Pending);
  assert(fixture.runtime.HasValidatedAutonomousCompletion());
  assert(fixture.checkCalls == 1 && fixture.journal.proofCalls == 1);
  fixture.timely = false; // Proof ACK latency must never re-sample timing.
  assert(fixture.Advance() == Progress::Pending);
  assert(fixture.checkCalls == 1);
  fixture.journal.proofAck = true;
  assert(fixture.Advance() == Progress::Succeeded);
  assert(fixture.journal.proven && fixture.journal.released == 2);
  assert(!fixture.runtime.HasValidatedAutonomousCompletion());
  assert(fixture.Advance() == Progress::Rejected);
  assert(!fixture.control.ConfirmTimedPresentation());

  CompletionFixture synchronous;
  synchronous.journal.synchronous = true;
  assert(synchronous.runtime.CompleteAutonomousPresentation(
      2, CompletionFixture::Check, &synchronous));
  assert(synchronous.checkCalls == 1 && synchronous.journal.proven);
  assert(!synchronous.runtime.CompleteAutonomousPresentation(
      2, CompletionFixture::Check, &synchronous));
}

void TestAsyncCompletionCancellationAndFailure() {
  using Progress = CompletionFixture::Progress;
  for (bool afterSeal : {false, true}) {
    CompletionFixture fixture;
    assert(fixture.Advance() == Progress::Pending);
    if (afterSeal) {
      fixture.journal.closeAck = true;
      assert(fixture.Advance() == Progress::Pending);
      assert(fixture.runtime.HasValidatedAutonomousCompletion());
    }
    SelectionStep step;
    fixture.runtime.Stop(step);
    assert(!fixture.runtime.HasValidatedAutonomousCompletion());
    fixture.journal.closeAck = fixture.journal.proofAck = true;
    fixture.runtime.Tick(0, step);
    assert(fixture.Advance() == Progress::Rejected);
    assert(!fixture.control.ConfirmTimedPresentation());
    assert(fixture.journal.proven == afterSeal);
    assert(fixture.checkCalls == (afterSeal ? 1u : 0u));
    assert(!fixture.journal.HasPendingStorage());
    assert(fixture.runtime.RecoverAfterInspection());
    assert(!fixture.runtime.HasValidatedAutonomousCompletion());
    assert(fixture.Advance() == Progress::Rejected);
  }
  for (bool failClose : {false, true}) {
    CompletionFixture fixture;
    fixture.journal.failClose = failClose;
    fixture.journal.failProof = !failClose;
    assert(fixture.Advance() == Progress::Pending);
    fixture.journal.closeAck = true;
    if (!failClose) {
      assert(fixture.Advance() == Progress::Pending);
      fixture.journal.proofAck = true;
    }
    assert(fixture.Advance() == Progress::Rejected);
    assert(fixture.runtime.NeedsRecovery());
    assert(!fixture.runtime.HasValidatedAutonomousCompletion());
    assert(!fixture.journal.proven);
    assert(fixture.checkCalls == (failClose ? 0u : 1u));
  }
  for (bool stopInCheck : {false, true}) {
    CompletionFixture fixture;
    fixture.timely = stopInCheck;
    fixture.stopInCheck = stopInCheck;
    assert(fixture.Advance() == Progress::Pending);
    fixture.journal.closeAck = true;
    assert(fixture.Advance() == Progress::Rejected);
    assert(fixture.runtime.NeedsRecovery());
    assert(fixture.checkCalls == 1 && fixture.journal.proofCalls == 0);
    assert(!fixture.runtime.HasValidatedAutonomousCompletion());
  }
  CompletionFixture fault;
  assert(fault.Advance() == Progress::Pending);
  fault.journal.closeAck = true;
  assert(fault.Advance() == Progress::Pending);
  fault.runtime.FaultAutonomousPresentation();
  assert(!fault.runtime.HasValidatedAutonomousCompletion());
  assert(fault.runtime.NeedsRecovery());
  fault.journal.proofAck = true;
  fault.journal.PollStorage();
  assert(fault.journal.proven && !fault.journal.HasPendingStorage());
  assert(fault.Advance() == Progress::Rejected);
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
  TestAsyncCompletionSeal();
  TestAsyncCompletionCancellationAndFailure();
}

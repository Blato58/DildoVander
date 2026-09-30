#include <cassert>

#include "../src/features/device_control/DeviceControlRuntime.h"
#include "../src/features/wand_presentation/WandPresentation.h"

namespace {

class FakeSafetyPort final : public SafetyPort {
 public:
  void DisableActuators() override { ++disabled; }
  void StartVibration(uint8_t) override {}
  void ReleaseLatch(uint8_t) override {}

  int disabled = 0;
};

class FakeJournal final : public CommandJournal {
 public:
  bool HasPending() const override { return pending; }
  uint8_t PendingCompartment() const override { return compartment; }
  uint8_t ReadyMask() const override { return readyMask; }
  bool Begin(const std::string& id, uint8_t selected) override {
    if (pending || failBegin) {
      return false;
    }
    pending = true;
    commandId = id;
    compartment = selected;
    return true;
  }
  bool Resolve(uint8_t nextMask) override {
    if (!pending || failResolve) {
      return false;
    }
    pending = false;
    compartment = 0;
    readyMask = nextMask;
    return true;
  }
  bool ResolveRestored(uint8_t restoredCompartment) override {
    if (!pending || failResolve || compartment != restoredCompartment ||
        (readyMask & (1u << (restoredCompartment - 1))) == 0) {
      return false;
    }
    pending = false;
    compartment = 0;
    return true;
  }
  bool PersistReadyMask(uint8_t nextMask) override {
    readyMask = nextMask;
    return true;
  }

  bool pending = false;
  bool failBegin = false;
  bool failResolve = false;
  uint8_t compartment = 0;
  uint8_t readyMask = 63;
  std::string commandId;
};

class FakePresentationPort final : public WandPresentationPort {
 public:
  FakePresentationPort(DeviceControlRuntime& runtime, DeviceControl& control,
                       SelectionSequence& selection)
      : runtime(runtime), control(control), selection(selection) {}

  bool IsReady(uint8_t) const override {
    return !control.NeedsRecovery() && !control.HasManualControl() &&
           !control.HasAutonomousPresentation();
  }
  bool IsSafeToContinue() const override {
    return !selection.NeedsRecovery();
  }
  bool IsRaised(uint8_t) const override { return raised; }
  bool IsResting(uint8_t) const override { return resting; }
  bool Raise(uint8_t compartment, const std::string& proposalId,
             const ConversationRuntimeSnapshot& snapshot) override {
    if (!runtime.BeginAutonomousPresentation(
            proposalId, compartment, snapshot)) {
      return false;
    }
    ++raises;
    resting = false;
    return true;
  }
  bool Lower(uint8_t) override {
    ++lowers;
    return true;
  }
  bool Complete(uint8_t compartment) override {
    ++completes;
    return runtime.CompleteAutonomousPresentation(compartment);
  }
  void DisableAndFault() override {
    ++faults;
    runtime.FaultAutonomousPresentation();
  }

  DeviceControlRuntime& runtime;
  DeviceControl& control;
  SelectionSequence& selection;
  bool raised = false;
  bool resting = true;
  int raises = 0;
  int lowers = 0;
  int completes = 0;
  int faults = 0;
};

struct Fixture {
  Fixture()
      : safety(safetyPort),
        selection(safety),
        control("device-1", "boot-1", selection, journal, 1),
        state(false, 0, ""),
        runtime(control, selection, state, "catalog-1"),
        port(runtime, control, selection),
        presentation(port) {
    safety.InitializeSafe();
    assert(runtime.BindPresentation(presentation));
    assert(runtime.RecoverAfterInspection());
  }

  ConversationRuntimeSnapshot Snapshot() const {
    ConversationRuntimeSnapshot snapshot;
    assert(state.Read(snapshot));
    return snapshot;
  }

  bool Queue(const std::string& proposalId, uint64_t nowMs,
             uint64_t lifetimeMs = 30000) {
    const ConversationRuntimeSnapshot snapshot = Snapshot();
    TurnProposal proposal;
    proposal.proposalId = proposalId;
    proposal.compartment = 3;
    proposal.catalogVersion = snapshot.catalogVersion;
    proposal.generation = snapshot.generation;
    proposal.deviceRevision = snapshot.deviceRevision;
    return pending.Store(proposal, snapshot, state, nowMs, lifetimeMs);
  }

  void TickThrough(uint64_t targetMs) {
    SelectionStep step;
    while (nextTickMs <= targetMs) {
      runtime.Tick(nextTickMs, step);
      nextTickMs += 10;
    }
  }

  FakeSafetyPort safetyPort;
  SafetyController safety;
  SelectionSequence selection;
  FakeJournal journal;
  DeviceControl control;
  ConversationRuntimeState state;
  DeviceControlRuntime runtime;
  PendingProposal pending;
  FakePresentationPort port;
  WandPresentation presentation;
  uint64_t nextTickMs = 1000;
};

void TestNormalCycleAndSecondProposal() {
  Fixture fixture;
  assert(fixture.Queue("proposal-1", 1000));
  const ConversationRuntimeSnapshot before = fixture.Snapshot();
  assert(fixture.presentation.Start(fixture.pending, fixture.state, 1000));
  assert(fixture.journal.pending && fixture.journal.commandId == "proposal-1");
  assert(fixture.runtime.HasAutonomousPresentation());
  assert(!fixture.state.Matches(before));
  assert(!fixture.runtime.AcquireManualControl("lease-1", 1001));
  fixture.port.raised = true;
  fixture.TickThrough(6200);
  assert(fixture.port.lowers == 1);
  fixture.port.resting = true;
  fixture.TickThrough(6300);
  assert(fixture.presentation.State() == WandPresentationState::Idle);
  assert(!fixture.journal.pending && fixture.journal.readyMask == 63);
  assert(!fixture.runtime.HasAutonomousPresentation());
  const ConversationRuntimeSnapshot after = fixture.Snapshot();
  assert(after.version > before.version);
  assert(after.deviceRevision > before.deviceRevision);
  assert(after.readyMask == 63);
  fixture.TickThrough(7000);
  assert(fixture.Queue("proposal-2", 7000));
  assert(fixture.presentation.Start(fixture.pending, fixture.state, 7000));
  assert(fixture.port.raises == 2);
}

void TestStopLeavesUnknownPosition() {
  Fixture fixture;
  assert(fixture.Queue("proposal-stop", 1000));
  assert(fixture.presentation.Start(fixture.pending, fixture.state, 1000));
  SelectionStep step;
  assert(fixture.runtime.Stop(step));
  assert(fixture.presentation.State() == WandPresentationState::Fault);
  assert(fixture.port.faults == 1);
  assert(fixture.journal.pending);
  ConversationRuntimeSnapshot snapshot;
  assert(!fixture.state.Read(snapshot));
  assert(!fixture.runtime.HasAutonomousPresentation());
  assert(!fixture.runtime.RecoverAfterInspection());
  fixture.port.resting = true;
  assert(fixture.runtime.RecoverAfterInspection());
  assert(fixture.state.Read(snapshot));
  assert((snapshot.readyMask & (1u << 2)) == 0);
  fixture.TickThrough(1100);
  assert(fixture.port.lowers == 0);
}

void TestStaleAndExpiredNeverStart() {
  Fixture fixture;
  assert(fixture.Queue("proposal-stale", 1000));
  assert(fixture.runtime.AcquireManualControl("lease-1", 1001));
  assert(!fixture.presentation.Start(fixture.pending, fixture.state, 1002));
  assert(fixture.port.raises == 0 && !fixture.journal.pending);

  Fixture expired;
  assert(expired.Queue("proposal-expired", 1000, 10));
  assert(!expired.presentation.Start(expired.pending, expired.state, 1010));
  assert(expired.port.raises == 0 && !expired.journal.pending);
}

void TestJournalFailuresStaySafe() {
  Fixture beginFailure;
  beginFailure.journal.failBegin = true;
  assert(beginFailure.Queue("proposal-begin-failure", 1000));
  assert(!beginFailure.presentation.Start(
      beginFailure.pending, beginFailure.state, 1000));
  assert(beginFailure.presentation.State() == WandPresentationState::Fault);
  assert(beginFailure.port.raises == 0);
  ConversationRuntimeSnapshot snapshot;
  assert(!beginFailure.state.Read(snapshot));

  Fixture completionFailure;
  assert(completionFailure.Queue("proposal-completion-failure", 1000));
  assert(completionFailure.presentation.Start(
      completionFailure.pending, completionFailure.state, 1000));
  completionFailure.port.raised = true;
  completionFailure.TickThrough(6200);
  completionFailure.port.resting = true;
  completionFailure.journal.failResolve = true;
  completionFailure.TickThrough(6300);
  assert(completionFailure.presentation.State() ==
         WandPresentationState::Fault);
  assert(completionFailure.journal.pending);
  assert(!completionFailure.state.Read(snapshot));
}

}  // namespace

int main() {
  TestNormalCycleAndSecondProposal();
  TestStopLeavesUnknownPosition();
  TestStaleAndExpiredNeverStart();
  TestJournalFailuresStaySafe();
}

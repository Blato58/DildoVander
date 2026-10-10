#include <cassert>

#include "../src/features/wand_presentation/WandPresentation.h"

namespace {

class FakePort final : public WandPresentationPort {
 public:
  bool ready = true;
  bool safe = true;
  bool raised = false;
  bool resting = true;
  int raises = 0;
  int lowers = 0;
  int completes = 0;
  int faults = 0;
  uint64_t raiseTimeoutMs = 3000;
  uint64_t lowerTimeoutMs = 3000;
  mutable int raisedChecks = 0;
  mutable int restingChecks = 0;

  bool IsReady(uint8_t) const override { return ready; }
  bool IsSafeToContinue() const override { return safe; }
  uint64_t RaiseTimeoutMs(uint8_t) const override { return raiseTimeoutMs; }
  uint64_t LowerTimeoutMs(uint8_t) const override { return lowerTimeoutMs; }
  bool IsRaised(uint8_t) const override { ++raisedChecks; return raised; }
  bool IsResting(uint8_t) const override { ++restingChecks; return resting; }
  bool Raise(uint8_t, const std::string&,
             const ConversationRuntimeSnapshot&) override {
    ++raises;
    resting = false;
    return true;
  }
  bool Lower(uint8_t) override {
    ++lowers;
    return true;
  }
  bool Complete(uint8_t) override {
    ++completes;
    return true;
  }
  void DisableAndFault() override { ++faults; }
};

TurnProposal Proposal(const std::string& id,
                      const ConversationRuntimeSnapshot& snapshot) {
  TurnProposal proposal;
  proposal.proposalId = id;
  proposal.compartment = 3;
  proposal.catalogVersion = snapshot.catalogVersion;
  proposal.generation = snapshot.generation;
  proposal.deviceRevision = snapshot.deviceRevision;
  return proposal;
}

struct Cycle {
  ConversationRuntimeState runtime{true, 63, "neutral-1"};
  ConversationRuntimeSnapshot snapshot;
  PendingProposal pending;
  FakePort port;
  WandPresentation presentation{port};

  Cycle() {
    assert(runtime.Read(snapshot));
    port.raiseTimeoutMs = 60000;
    port.lowerTimeoutMs = 60000;
  }

  void Start(const std::string& id, uint64_t atMs) {
    port.raised = false;
    assert(pending.Store(Proposal(id, snapshot), snapshot, runtime, atMs, 30000));
    assert(presentation.Start(pending, runtime, atMs));
  }
};

void DeadlinePrecedesAllPhaseTransitions() {
  // Raising includes the score port's preparation. Even permissive port phase
  // allowances must not extend the absolute presentation deadline.
  for (const auto phase : {WandPresentationState::Raising,
                           WandPresentationState::Showing,
                           WandPresentationState::Lowering}) {
    Cycle cycle;
    cycle.Start("deadline", 1000);
    if (phase != WandPresentationState::Raising) {
      cycle.port.raised = true;
      cycle.presentation.Tick(1100);
    }
    if (phase == WandPresentationState::Lowering) {
      cycle.presentation.Tick(6100);
    }
    assert(cycle.presentation.State() == phase);
    cycle.port.raised = true;
    cycle.port.resting = true;
    const int raisedChecks = cycle.port.raisedChecks;
    const int restingChecks = cycle.port.restingChecks;
    const int lowers = cycle.port.lowers;
    cycle.presentation.Tick(16000);
    assert(cycle.presentation.State() == WandPresentationState::Fault);
    assert(cycle.port.faults == 1 && cycle.port.completes == 0);
    assert(cycle.port.raisedChecks == raisedChecks);
    assert(cycle.port.restingChecks == restingChecks);
    assert(cycle.port.lowers == lowers);
    cycle.presentation.Tick(16001);
    assert(cycle.port.faults == 1 && cycle.port.completes == 0);
  }
}

void LateRestingDoesNotComplete() {
  Cycle cycle;
  cycle.Start("late-rest", 1000);
  cycle.port.raised = true;
  cycle.presentation.Tick(10000);
  cycle.presentation.Tick(15000);
  assert(cycle.presentation.State() == WandPresentationState::Lowering);
  cycle.port.resting = true;
  // Lowering has only just begun, but admission was over fifteen seconds ago.
  cycle.presentation.Tick(16001);
  assert(cycle.presentation.State() == WandPresentationState::Fault);
  assert(cycle.port.completes == 0 && cycle.port.faults == 1);
}

void DeadlineGuardDoesNotAdvancePhasesAndDoesNotSlide() {
  Cycle cycle;
  cycle.Start("absolute", 1000);
  cycle.port.raised = true;
  cycle.presentation.CheckDeadline(15999);
  assert(cycle.presentation.State() == WandPresentationState::Raising);
  assert(cycle.port.raisedChecks == 0 && cycle.port.lowers == 0);
  assert(!cycle.presentation.Start(cycle.pending, cycle.runtime, 15999));
  cycle.presentation.CheckDeadline(16000);
  assert(cycle.presentation.State() == WandPresentationState::Fault);
  assert(cycle.port.faults == 1 && cycle.port.completes == 0);
}

void NewCyclesReceiveANewDeadline() {
  Cycle cycle;
  cycle.Start("under-limit", 1000);
  cycle.port.raised = true;
  cycle.presentation.Tick(5500);
  cycle.presentation.Tick(10500);
  cycle.port.resting = true;
  cycle.presentation.Tick(15999);
  assert(cycle.presentation.State() == WandPresentationState::Idle);
  assert(cycle.port.completes == 1 && cycle.port.faults == 0);
  cycle.presentation.CheckDeadline(20000);
  assert(cycle.presentation.State() == WandPresentationState::Idle);
  cycle.Start("second-cycle", 30000);
  cycle.presentation.CheckDeadline(44999);
  assert(cycle.presentation.State() == WandPresentationState::Raising);
  cycle.presentation.Tick(45000);
  assert(cycle.presentation.State() == WandPresentationState::Fault);
  assert(cycle.port.faults == 1 && cycle.port.completes == 1);
  cycle.port.resting = true;
  assert(cycle.presentation.RecoverAfterInspection(true));
  cycle.Start("after-inspection", 60000);
  cycle.port.raised = true;
  cycle.presentation.Tick(60100);
  cycle.presentation.Tick(65100);
  cycle.port.resting = true;
  cycle.presentation.Tick(65200);
  assert(cycle.presentation.State() == WandPresentationState::Idle);
  assert(cycle.port.completes == 2 && cycle.port.faults == 1);
}

void ClockRegressionFaultsWithinAPhase() {
  Cycle cycle;
  cycle.Start("clock-order", 1000);
  cycle.presentation.CheckDeadline(1200);
  cycle.presentation.Tick(1199);
  assert(cycle.presentation.State() == WandPresentationState::Fault);
  assert(cycle.port.faults == 1 && cycle.port.completes == 0);
}

}  // namespace

int main() {
  DeadlinePrecedesAllPhaseTransitions();
  LateRestingDoesNotComplete();
  DeadlineGuardDoesNotAdvancePhasesAndDoesNotSlide();
  NewCyclesReceiveANewDeadline();
  ClockRegressionFaultsWithinAPhase();
  ConversationRuntimeState runtime(true, 63, "neutral-1");
  ConversationRuntimeSnapshot snapshot;
  assert(runtime.Read(snapshot));
  PendingProposal pending;
  FakePort port;
  WandPresentation presentation(port);
  assert(pending.Store(Proposal("first", snapshot), snapshot, runtime,
                       1000, 30000));
  assert(port.raises == 0);
  assert(presentation.Start(pending, runtime, 1000));
  assert(port.raises == 1);
  assert(!presentation.Start(pending, runtime, 1001));
  port.raised = true;
  presentation.Tick(1200);
  assert(presentation.State() == WandPresentationState::Showing);
  presentation.Tick(6200);
  assert(presentation.State() == WandPresentationState::Lowering);
  assert(port.lowers == 1);
  port.resting = true;
  presentation.Tick(6300);
  assert(presentation.State() == WandPresentationState::Idle);
  assert(port.completes == 1 && port.faults == 0);
  assert(pending.Store(Proposal("first", snapshot), snapshot, runtime,
                       6400, 30000));
  assert(!presentation.Start(pending, runtime, 6400));
  assert(port.raises == 1);
  pending.Clear();

  assert(pending.Store(Proposal("second", snapshot), snapshot, runtime,
                       7000, 30000));
  assert(presentation.Start(pending, runtime, 7000));
  presentation.Stop();
  assert(presentation.State() == WandPresentationState::Fault);
  assert(port.faults == 1);
  assert(!presentation.Start(pending, runtime, 7001));
  assert(!presentation.RecoverAfterInspection(true));
  port.resting = true;
  assert(!presentation.RecoverAfterInspection(false));
  assert(presentation.RecoverAfterInspection(true));
  assert(presentation.State() == WandPresentationState::Idle);

  assert(pending.Store(Proposal("third", snapshot), snapshot, runtime,
                       8000, 30000));
  assert(presentation.Start(pending, runtime, 8000));
  port.raised = false;
  presentation.Tick(11000);
  assert(presentation.State() == WandPresentationState::Fault);
  assert(port.faults == 2);
  port.resting = true;
  assert(presentation.RecoverAfterInspection(true));
  assert(runtime.Publish(0, 1, 63, "neutral-1"));
  assert(pending.Store(Proposal("fourth", snapshot), snapshot, runtime,
                       12000, 30000) == false);
  assert(port.raises == 3);

  assert(runtime.Read(snapshot));
  assert(pending.Store(Proposal("fifth", snapshot), snapshot, runtime,
                       13000, 30000));
  assert(presentation.Start(pending, runtime, 13000));
  port.safe = false;
  presentation.Tick(13001);
  assert(presentation.State() == WandPresentationState::Fault);
  assert(port.faults == 3);
  port.safe = true;
  port.resting = true;
  assert(presentation.RecoverAfterInspection(true));
  assert(pending.Store(Proposal("sixth", snapshot), snapshot, runtime,
                       14000, 30000));
  assert(presentation.Start(pending, runtime, 14000));
  presentation.Tick(13999);
  assert(presentation.State() == WandPresentationState::Fault);
  assert(port.faults == 4);
  port.resting = true;
  assert(presentation.RecoverAfterInspection(true));
  presentation.Stop();
  assert(presentation.State() == WandPresentationState::Fault);
  assert(port.faults == 5);
  assert(!presentation.RecoverAfterInspection(false));
  assert(presentation.RecoverAfterInspection(true));
}

#include <cassert>

#include "../src/features/wand_presentation/WandPresentation.h"

namespace {

class FakePort : public WandPresentationPort {
 public:
  bool ready = true;
  bool safe = true;
  bool raised = false;
  bool resting = true;
  int raises = 0;
  int lowers = 0;
  int completes = 0;
  int faults = 0;
  bool completionPending = false;
  bool timingValidated = false;
  bool completionFailed = false;
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
    return !completionPending && !completionFailed;
  }
  bool CompletionIsPending() const override { return completionPending; }
  bool CompletionTimingValidated() const override { return timingValidated; }
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
  proposal.operationEpoch = snapshot.operationEpoch;
  return proposal;
}

class AsyncPort final : public FakePort {
 public:
  WandAdmissionProgress reserveProgress = WandAdmissionProgress::Pending;
  WandAdmissionProgress beginProgress = WandAdmissionProgress::Pending;
  mutable int fingerprintCaptures = 0;
  mutable int calibrationCaptures = 0;
  int reserves = 0;
  int begins = 0;
  int cancellations = 0;
  uint64_t reportedFingerprint = 101;
  uint64_t reportedCalibration = 202;
  uint64_t firstBeginAtMs = 0;
  uint64_t lastBeginAtMs = 0;
  uint64_t deadlineMs = 0;
  bool claimedValid = true;
  std::string originalId;
  ConversationRuntimeSnapshot originalSnapshot;

  uint64_t CurrentOperationFingerprint() const override {
    ++fingerprintCaptures;
    return reportedFingerprint;
  }
  uint64_t CurrentCalibrationRevision() const override {
    ++calibrationCaptures;
    return reportedCalibration;
  }
  bool ValidateClaimedOperation(const PendingProposal::ClaimedProposal&,
      uint64_t, uint64_t, uint64_t) const override { return claimedValid; }
  void CheckOriginal(const PendingProposal::ClaimedProposal& claim,
                     uint64_t fingerprint, uint64_t calibration) {
    if (originalId.empty()) {
      originalId = claim.Proposal().proposalId;
      originalSnapshot = claim.Snapshot();
      deadlineMs = claim.DeadlineMs();
    }
    assert(claim.Proposal().proposalId == originalId);
    assert(claim.Snapshot().generation == originalSnapshot.generation);
    assert(claim.Snapshot().deviceRevision == originalSnapshot.deviceRevision);
    assert(claim.Snapshot().version == originalSnapshot.version);
    assert(claim.Snapshot().operationEpoch == originalSnapshot.operationEpoch);
    assert(claim.Snapshot().catalogVersion == originalSnapshot.catalogVersion);
    assert(claim.Snapshot().readyMask == originalSnapshot.readyMask);
    assert(claim.DeadlineMs() == deadlineMs);
    assert(fingerprint == 101 && calibration == 202);
  }
  WandAdmissionProgress ReserveClaimedOperation(
      const PendingProposal::ClaimedProposal& claim, uint64_t fingerprint,
      uint64_t calibration, uint64_t nowMs) override {
    ++reserves;
    CheckOriginal(claim, fingerprint, calibration);
    assert(claim.IsLive(nowMs));
    return reserveProgress;
  }
  WandAdmissionProgress BeginClaimedOperation(
      const PendingProposal::ClaimedProposal& claim, uint64_t fingerprint,
      uint64_t calibration, uint64_t acceptedAtMs, uint64_t nowMs) override {
    if (begins++ == 0) firstBeginAtMs = acceptedAtMs;
    lastBeginAtMs = acceptedAtMs;
    CheckOriginal(claim, fingerprint, calibration);
    assert(claim.IsLive(nowMs));
    if (beginProgress == WandAdmissionProgress::Succeeded)
      assert(Raise(claim.Proposal().compartment, claim.Proposal().proposalId, claim.Snapshot()));
    return beginProgress;
  }
  void CancelClaimedOperation() override { ++cancellations; }
};

void ClaimedAdmissionRetainsTheOriginalIntentWithoutPublicSnapshot() {
  ConversationRuntimeState runtime(true, 63, "neutral-1");
  assert(runtime.Publish(4, 5, 63, "neutral-1", 6));
  ConversationRuntimeSnapshot snapshot;
  assert(runtime.Read(snapshot));
  PendingProposal pending;
  AsyncPort port;
  WandPresentation presentation(port);
  assert(pending.Store(Proposal("claimed", snapshot), snapshot, runtime, 1000, 9000));
  assert(!presentation.ReservePendingOperation(pending, runtime, 1100));
  assert(presentation.State() == WandPresentationState::AwaitingAdmission);
  assert(presentation.AdmissionIsPending());
  assert(port.raises == 0 && port.begins == 0 && port.reserves == 1);
  TurnProposal visible;
  assert(!pending.Read(visible, runtime, 1100));
  assert(presentation.ReadClaimedProposal(visible) && visible.proposalId == "claimed");
  assert(presentation.ClaimedOperationFingerprint() == 101);
  assert(presentation.ClaimedCalibrationRevision() == 202);
  ConversationRuntimeState replacementRuntime(true, 63, "neutral-1");
  ConversationRuntimeSnapshot replacementSnapshot;
  assert(replacementRuntime.Read(replacementSnapshot));
  assert(pending.Store(Proposal("newer", replacementSnapshot), replacementSnapshot,
      replacementRuntime, 1200, 9000));
  runtime.Invalidate();
  port.reportedFingerprint = 303;
  port.reportedCalibration = 404;
  presentation.Tick(1300);
  assert(port.reserves == 2 && port.begins == 0 && port.raises == 0);
  port.reserveProgress = WandAdmissionProgress::Succeeded;
  assert(presentation.ReservePendingOperation(pending, runtime, 1400));
  assert(!presentation.AdmissionIsPending());
  presentation.Tick(1500);
  assert(port.begins == 0 && port.raises == 0);
  assert(!presentation.Start(pending, runtime, 1600));
  assert(presentation.AdmissionIsPending());
  assert(port.begins == 1 && port.raises == 0);
  presentation.Tick(1700);
  assert(port.begins == 2 && port.firstBeginAtMs == 1600 && port.lastBeginAtMs == 1600);
  port.beginProgress = WandAdmissionProgress::Succeeded;
  presentation.Tick(1800);
  assert(presentation.State() == WandPresentationState::Raising);
  assert(port.raises == 1 && port.begins == 3);
  assert(port.firstBeginAtMs == 1600 && port.lastBeginAtMs == 1600);
  assert(port.fingerprintCaptures == 1 && port.calibrationCaptures == 1);
  assert(!presentation.ReadClaimedProposal(visible));
  // The unrelated replacement remained pending; the retained claim never read it.
  assert(pending.Read(visible, replacementRuntime, 1800));
  assert(visible.proposalId == "newer");
  assert(port.originalId == "claimed" && port.deadlineMs == 10000);
}

void ClaimedAdmissionExpiryStopAndRejectionNeverMove() {
  for (int mode = 0; mode < 7; ++mode) {
    ConversationRuntimeState runtime(true, 63, "neutral-1");
    assert(runtime.Publish(1, 1, 63, "neutral-1", 1));
    ConversationRuntimeSnapshot snapshot;
    assert(runtime.Read(snapshot));
    PendingProposal pending;
    AsyncPort port;
    WandPresentation presentation(port);
    assert(pending.Store(Proposal("admission-end", snapshot), snapshot, runtime,
        1000, mode == 6 ? 1000 : 30000));
    if (mode == 1 || mode == 3 || mode == 5 || mode == 6)
      port.reserveProgress = WandAdmissionProgress::Succeeded;
    assert(!presentation.Start(pending, runtime, 1100));
    assert(presentation.AdmissionIsPending());
    if (mode == 0 || mode == 1) presentation.Stop();
    else if (mode == 2) presentation.Tick(31000);
    else if (mode == 3) presentation.Tick(16100);
    else if (mode == 5) { port.claimedValid = false; presentation.Tick(1200); }
    else if (mode == 6) presentation.Tick(2000);
    else presentation.Tick(1099);
    assert(presentation.State() == WandPresentationState::Fault);
    assert(!presentation.AdmissionIsPending());
    TurnProposal visible;
    assert(!presentation.ReadClaimedProposal(visible));
    port.reserveProgress = port.beginProgress = WandAdmissionProgress::Succeeded;
    presentation.Tick(32000);
    assert(port.raises == 0 && port.lowers == 0 && port.completes == 0);
    assert(port.cancellations == 1 && port.faults == 1);
  }

  ConversationRuntimeState runtime(true, 63, "neutral-1");
  assert(runtime.Publish(1, 1, 63, "neutral-1", 1));
  ConversationRuntimeSnapshot snapshot;
  assert(runtime.Read(snapshot));
  PendingProposal pending;
  AsyncPort port;
  WandPresentation presentation(port);
  assert(pending.Store(Proposal("rejected", snapshot), snapshot, runtime, 1000, 1000));
  port.reserveProgress = WandAdmissionProgress::Rejected;
  assert(!presentation.ReservePendingOperation(pending, runtime, 1100));
  assert(!presentation.AdmissionIsPending());
  assert(presentation.State() == WandPresentationState::Idle);
  assert(port.raises == 0 && port.cancellations == 1);
}

void ProducerPolledAdmissionChecksItsGatesBeforeConsumingAck() {
  for (bool cancelBeforeAck : {false, true}) {
    ConversationRuntimeState runtime(true, 63, "neutral-1");
    assert(runtime.Publish(1, 1, 63, "neutral-1", 1));
    ConversationRuntimeSnapshot snapshot;
    assert(runtime.Read(snapshot));
    PendingProposal pending;
    AsyncPort port;
    WandPresentation presentation(port);
    port.reserveProgress = WandAdmissionProgress::Succeeded;
    assert(pending.Store(Proposal("producer-polled", snapshot), snapshot, runtime, 1000, 9000));
    assert(presentation.ReservePendingOperation(pending, runtime, 1100));
    assert(!presentation.StartPolled(pending, runtime, 1200));
    assert(port.begins == 1 && port.raises == 0 && presentation.AdmissionIsPending());
    runtime.Invalidate();
    port.beginProgress = WandAdmissionProgress::Succeeded;
    presentation.Tick(1300);
    assert(port.begins == 1 && port.raises == 0);
    // The producer checks cancellation/source authority here. An available
    // durable ACK by itself cannot make Tick begin motion ahead of that check.
    if (cancelBeforeAck) {
      presentation.CancelPendingReservation();
      presentation.Tick(1400);
      assert(presentation.State() == WandPresentationState::Idle && port.raises == 0);
    } else {
      assert(presentation.StartPolled(pending, runtime, 1400));
      assert(presentation.State() == WandPresentationState::Raising && port.raises == 1);
      assert(port.firstBeginAtMs == 1200 && port.lastBeginAtMs == 1200);
    }
  }
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

void CompletionWaitPreservesThePhysicalDeadlineAndStop() {
  for (bool sealed : {false, true}) {
    Cycle cycle;
    cycle.Start("await-proof", 1000);
    cycle.port.raised = true;
    cycle.presentation.Tick(1100);
    cycle.presentation.Tick(6100);
    cycle.port.resting = true;
    cycle.port.completionPending = true;
    cycle.presentation.Tick(6200);
    assert(cycle.presentation.State() == WandPresentationState::Completing);
    assert(cycle.port.raises == 1 && cycle.port.lowers == 1 && cycle.port.completes == 1);
    cycle.port.timingValidated = sealed;
    cycle.presentation.Tick(16000);
    assert(cycle.presentation.State() == (sealed
        ? WandPresentationState::Completing : WandPresentationState::Fault));
    if (!sealed) continue;
    cycle.presentation.Stop();
    const int completedCalls = cycle.port.completes;
    cycle.port.completionPending = false; // Late successful durable proof.
    cycle.presentation.Tick(16010);
    assert(cycle.presentation.State() == WandPresentationState::Fault);
    assert(cycle.port.completes == completedCalls && cycle.port.faults == 1);
  }
  Cycle completed;
  completed.Start("late-proof-ack", 1000);
  completed.port.raised = true;
  completed.presentation.Tick(1100);
  completed.presentation.Tick(6100);
  completed.port.resting = true;
  completed.port.completionPending = true;
  completed.presentation.Tick(6200);
  completed.port.timingValidated = true;
  completed.presentation.Tick(30000);
  assert(completed.presentation.State() == WandPresentationState::Completing);
  completed.port.completionPending = false;
  completed.presentation.Tick(30010);
  assert(completed.presentation.State() == WandPresentationState::Idle);
  assert(completed.port.raises == 1 && completed.port.lowers == 1 && completed.port.faults == 0);
}

void CompletionWaitStillRejectsHealthClockAndAckFailure() {
  for (int fault = 0; fault < 3; ++fault) {
    Cycle cycle;
    cycle.Start("closing-fault", 1000);
    cycle.port.raised = true;
    cycle.presentation.Tick(1100);
    cycle.presentation.Tick(6100);
    cycle.port.resting = true;
    cycle.port.completionPending = true;
    cycle.presentation.Tick(6200);
    cycle.port.timingValidated = true;
    if (fault == 0) cycle.port.safe = false;
    if (fault == 1) { cycle.port.completionPending = false; cycle.port.completionFailed = true; }
    cycle.presentation.Tick(fault == 2 ? 6199 : 6210);
    assert(cycle.presentation.State() == WandPresentationState::Fault);
    assert(cycle.port.faults == 1 && cycle.port.raises == 1 && cycle.port.lowers == 1);
  }
}

}  // namespace

int main() {
  ProducerPolledAdmissionChecksItsGatesBeforeConsumingAck();
  ClaimedAdmissionRetainsTheOriginalIntentWithoutPublicSnapshot();
  ClaimedAdmissionExpiryStopAndRejectionNeverMove();
  DeadlinePrecedesAllPhaseTransitions();
  LateRestingDoesNotComplete();
  DeadlineGuardDoesNotAdvancePhasesAndDoesNotSlide();
  NewCyclesReceiveANewDeadline();
  ClockRegressionFaultsWithinAPhase();
  CompletionWaitPreservesThePhysicalDeadlineAndStop();
  CompletionWaitStillRejectsHealthClockAndAckFailure();
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

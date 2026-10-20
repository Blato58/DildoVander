#include "WandPresentation.h"
#include "../physical_effects/PresentationScore.h"

namespace {

constexpr uint64_t ShowDurationMs = PresentationScore::ShowMs;

bool Elapsed(uint64_t nowMs, uint64_t startedAtMs, uint64_t durationMs) {
  return nowMs - startedAtMs >= durationMs;
}

}  // namespace

WandPresentation::WandPresentation(WandPresentationPort& port) : port_(port) {}

uint64_t WandPresentation::CurrentOperationFingerprint() const {
  return state_ == WandPresentationState::Idle ? port_.CurrentOperationFingerprint() : 0;
}

bool WandPresentation::ReservePendingOperation(
    PendingProposal& pending, const ConversationRuntimeState& runtime, uint64_t nowMs) {
  TurnProposal proposal;
  ConversationRuntimeSnapshot snapshot;
  if (state_ != WandPresentationState::Idle || !pending.Read(proposal, runtime, nowMs) ||
      !runtime.Read(snapshot) || proposal.proposalId.empty() ||
      proposal.compartment < 1 || proposal.compartment > 6 ||
      proposal.generation != snapshot.generation || proposal.deviceRevision != snapshot.deviceRevision ||
      proposal.catalogVersion != snapshot.catalogVersion || proposal.operationEpoch != snapshot.operationEpoch ||
      (proposal.proposalId == lastProposalId_ && proposal.operationEpoch == lastProposalEpoch_) ||
      !port_.IsReady(proposal.compartment) || !port_.IsSafeToContinue() ||
      !port_.IsResting(proposal.compartment)) return false;
  return proposal.operationEpoch == 0 || port_.ReserveOperation(proposal.compartment, proposal.proposalId, snapshot);
}

bool WandPresentation::Start(
    PendingProposal& pending,
    const ConversationRuntimeState& runtime,
    uint64_t nowMs) {
  if (state_ != WandPresentationState::Idle) {
    return false;
  }
  TurnProposal preview;
  if (!pending.Read(preview, runtime, nowMs) ||
      preview.compartment < 1 || preview.compartment > 6 ||
      preview.proposalId.empty() ||
      (preview.proposalId == lastProposalId_ && preview.operationEpoch == lastProposalEpoch_) ||
      !port_.IsReady(preview.compartment) ||
      !port_.IsSafeToContinue() ||
      !port_.IsResting(preview.compartment)) {
    return false;
  }
  if (preview.operationEpoch != 0 && !ReservePendingOperation(pending, runtime, nowMs)) return false;
  TurnProposal proposal;
  ConversationRuntimeSnapshot snapshot;
  if (!pending.Consume(proposal, snapshot, runtime, nowMs) ||
      proposal.proposalId != preview.proposalId ||
      !runtime.Matches(snapshot) ||
      (snapshot.readyMask & (1u << (proposal.compartment - 1))) == 0 ||
      proposal.catalogVersion != snapshot.catalogVersion ||
      proposal.generation != snapshot.generation ||
      proposal.deviceRevision != snapshot.deviceRevision ||
      proposal.operationEpoch != snapshot.operationEpoch) {
    if (preview.operationEpoch != 0) CancelPendingReservation();
    return false;
  }
  lastProposalId_ = proposal.proposalId;
  lastProposalEpoch_ = proposal.operationEpoch;
  compartment_ = proposal.compartment;
  phaseStartedAtMs_ = nowMs;
  presentationStartedAtMs_ = nowMs;
  lastObservedAtMs_ = nowMs;
  port_.ObserveOwnerTime(nowMs);
  if (!port_.Raise(compartment_, proposal.proposalId, snapshot)) {
    if (proposal.operationEpoch != 0) CancelPendingReservation();
    if (port_.AdmissionWasDuplicateOrConflict()) {
      compartment_ = 0;
      return false;
    }
    Fault();
    return false;
  }
  state_ = WandPresentationState::Raising;
  return true;
}

void WandPresentation::Tick(uint64_t nowMs) {
  CheckDeadline(nowMs);
  if (state_ != WandPresentationState::Idle &&
      state_ != WandPresentationState::Fault &&
      (nowMs < phaseStartedAtMs_ || !port_.IsSafeToContinue())) {
    Fault();
    return;
  }
  switch (state_) {
    case WandPresentationState::Idle:
    case WandPresentationState::Fault:
      return;
    case WandPresentationState::Raising:
      if (Elapsed(nowMs, phaseStartedAtMs_, port_.RaiseTimeoutMs(compartment_))) {
        Fault();
      } else if (port_.IsRaised(compartment_)) {
        state_ = WandPresentationState::Showing;
        phaseStartedAtMs_ = nowMs;
      }
      return;
    case WandPresentationState::Showing:
      if (Elapsed(nowMs, phaseStartedAtMs_, ShowDurationMs)) {
        port_.ObserveOwnerTime(nowMs);
        if (!port_.Lower(compartment_)) {
          Fault();
          return;
        }
        state_ = WandPresentationState::Lowering;
        phaseStartedAtMs_ = nowMs;
      }
      return;
    case WandPresentationState::Lowering:
      if (Elapsed(nowMs, phaseStartedAtMs_, port_.LowerTimeoutMs(compartment_))) {
        Fault();
      } else if (port_.IsResting(compartment_)) {
        port_.ObserveOwnerTime(nowMs);
        if (!port_.Complete(compartment_)) {
          Fault();
          return;
        }
        compartment_ = 0;
        state_ = WandPresentationState::Idle;
      }
      return;
  }
}

void WandPresentation::CheckDeadline(uint64_t nowMs) {
  if (state_ == WandPresentationState::Idle ||
      state_ == WandPresentationState::Fault) {
    return;
  }
  // Check clock order before unsigned subtraction; phase changes cannot extend
  // the whole-cycle budget or permit a late completion to restore availability.
  if (nowMs < presentationStartedAtMs_ || nowMs < phaseStartedAtMs_ ||
      nowMs < lastObservedAtMs_ ||
      Elapsed(nowMs, presentationStartedAtMs_, MaximumDurationMs)) {
    Fault();
    return;
  }
  lastObservedAtMs_ = nowMs;
}

void WandPresentation::Stop() {
  if (state_ != WandPresentationState::Fault) {
    Fault();
  }
}

bool WandPresentation::RecoverAfterInspection(bool inspected) {
  if (state_ != WandPresentationState::Fault || !inspected ||
      !port_.IsSafeToContinue() ||
      (compartment_ != 0 &&
       (!port_.IsReady(compartment_) || !port_.IsResting(compartment_)))) {
    return false;
  }
  compartment_ = 0;
  state_ = WandPresentationState::Idle;
  return true;
}

WandPresentationState WandPresentation::State() const { return state_; }

uint8_t WandPresentation::Compartment() const { return compartment_; }

void WandPresentation::Fault() {
  port_.DisableAndFault();
  state_ = WandPresentationState::Fault;
}

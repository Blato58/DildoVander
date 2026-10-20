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

bool WandPresentation::CaptureClaim(
    PendingProposal& pending, const ConversationRuntimeState& runtime, uint64_t nowMs) {
  if (state_ == WandPresentationState::AwaitingAdmission) return true;
  if (state_ != WandPresentationState::Idle || !pending.Claim(claimed_, runtime, nowMs)) return false;
  const auto& proposal = claimed_.Proposal();
  if ((proposal.proposalId == lastProposalId_ && proposal.operationEpoch == lastProposalEpoch_) ||
      !port_.IsReady(proposal.compartment) || !port_.IsSafeToContinue() ||
      !port_.IsResting(proposal.compartment)) {
    claimed_.Clear();
    return false;
  }
  claimedFingerprint_ = port_.CurrentOperationFingerprint();
  claimedCalibrationRevision_ = port_.CurrentCalibrationRevision();
  compartment_ = proposal.compartment;
  phaseStartedAtMs_ = lastObservedAtMs_ = nowMs;
  state_ = WandPresentationState::AwaitingAdmission;
  return true;
}

bool WandPresentation::ReadClaimedProposal(TurnProposal& proposal) const {
  if (!claimed_.IsClaimed()) return false;
  proposal = claimed_.Proposal();
  return true;
}

void WandPresentation::ClearAdmission() {
  claimed_.Clear();
  claimedFingerprint_ = claimedCalibrationRevision_ = 0;
  admissionPending_ = reservationReady_ = beginRequested_ = beginAttempted_ = false;
  automaticContinuation_ = true;
}

void WandPresentation::CancelPendingReservation() {
  port_.CancelClaimedOperation();
  ClearAdmission();
  if (state_ == WandPresentationState::AwaitingAdmission) {
    compartment_ = 0;
    state_ = WandPresentationState::Idle;
  }
}

bool WandPresentation::AdvanceReservation(uint64_t nowMs) {
  CheckDeadline(nowMs);
  if (state_ != WandPresentationState::AwaitingAdmission) return false;
  if (!port_.IsSafeToContinue() || !port_.IsReady(compartment_) ||
      !port_.IsResting(compartment_) ||
      !port_.ValidateClaimedOperation(claimed_, claimedFingerprint_,
          claimedCalibrationRevision_, nowMs)) {
    Fault();
    return false;
  }
  if (reservationReady_) return true;
  // Legacy epoch-zero operations have no scoped reserve transaction.
  const auto progress = claimed_.Proposal().operationEpoch == 0
      ? WandAdmissionProgress::Succeeded
      : port_.ReserveClaimedOperation(claimed_, claimedFingerprint_,
          claimedCalibrationRevision_, nowMs);
  admissionPending_ = progress == WandAdmissionProgress::Pending;
  if (progress == WandAdmissionProgress::Rejected) {
    CancelPendingReservation();
    return false;
  }
  reservationReady_ = progress == WandAdmissionProgress::Succeeded;
  return reservationReady_;
}

bool WandPresentation::ReservePendingOperation(
    PendingProposal& pending, const ConversationRuntimeState& runtime, uint64_t nowMs) {
  return CaptureClaim(pending, runtime, nowMs) && AdvanceReservation(nowMs);
}

bool WandPresentation::AdvanceBegin(uint64_t nowMs) {
  if (!AdvanceReservation(nowMs)) return false;
  if (!beginAttempted_) {
    presentationStartedAtMs_ = phaseStartedAtMs_ = nowMs;
    beginAttempted_ = true;
  }
  port_.ObserveOwnerTime(nowMs);
  const auto progress = port_.BeginClaimedOperation(claimed_, claimedFingerprint_,
      claimedCalibrationRevision_, presentationStartedAtMs_, nowMs);
  admissionPending_ = progress == WandAdmissionProgress::Pending;
  if (progress == WandAdmissionProgress::Pending) return false;
  if (progress == WandAdmissionProgress::Rejected) {
    if (port_.AdmissionWasDuplicateOrConflict()) CancelPendingReservation();
    else Fault();
    return false;
  }
  if (!claimed_.IsLive(nowMs)) {
    Fault();
    return false;
  }
  lastProposalId_ = claimed_.Proposal().proposalId;
  lastProposalEpoch_ = claimed_.Proposal().operationEpoch;
  ClearAdmission();
  state_ = WandPresentationState::Raising;
  return true;
}

bool WandPresentation::Start(
    PendingProposal& pending, const ConversationRuntimeState& runtime, uint64_t nowMs) {
  return RequestBegin(pending, runtime, nowMs, true);
}

bool WandPresentation::StartPolled(
    PendingProposal& pending, const ConversationRuntimeState& runtime, uint64_t nowMs) {
  return RequestBegin(pending, runtime, nowMs, false);
}

bool WandPresentation::RequestBegin(PendingProposal& pending,
    const ConversationRuntimeState& runtime, uint64_t nowMs, bool automaticContinuation) {
  if (!CaptureClaim(pending, runtime, nowMs)) return false;
  if (beginRequested_ && automaticContinuation_ != automaticContinuation) return false;
  automaticContinuation_ = automaticContinuation;
  beginRequested_ = true;
  return AdvanceBegin(nowMs);
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
    case WandPresentationState::AwaitingAdmission:
      if (beginRequested_ && automaticContinuation_) AdvanceBegin(nowMs);
      else if (beginRequested_) return;
      else AdvanceReservation(nowMs);
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
          if (port_.CompletionIsPending()) state_ = WandPresentationState::Completing;
          else Fault();
          return;
        }
        compartment_ = 0;
        state_ = WandPresentationState::Idle;
      }
      return;
    case WandPresentationState::Completing:
      port_.ObserveOwnerTime(nowMs);
      if (!port_.Complete(compartment_)) {
        if (!port_.CompletionIsPending()) Fault();
        return;
      }
      compartment_ = 0;
      state_ = WandPresentationState::Idle;
      return;
  }
}

void WandPresentation::CheckDeadline(uint64_t nowMs) {
  if (state_ == WandPresentationState::Idle ||
      state_ == WandPresentationState::Fault) {
    return;
  }
  if (state_ == WandPresentationState::AwaitingAdmission) {
    if (!claimed_.IsLive(nowMs) || nowMs < lastObservedAtMs_ ||
        (beginAttempted_ && Elapsed(nowMs, presentationStartedAtMs_, MaximumDurationMs))) {
      Fault();
      return;
    }
    lastObservedAtMs_ = nowMs;
    return;
  }
  // Check clock order before unsigned subtraction; phase changes cannot extend
  // the whole-cycle budget or permit a late completion to restore availability.
  if (nowMs < presentationStartedAtMs_ || nowMs < phaseStartedAtMs_ ||
      nowMs < lastObservedAtMs_ ||
      (!(state_ == WandPresentationState::Completing && port_.CompletionTimingValidated()) &&
       Elapsed(nowMs, presentationStartedAtMs_, MaximumDurationMs))) {
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
  port_.CancelClaimedOperation();
  ClearAdmission();
  port_.DisableAndFault();
  state_ = WandPresentationState::Fault;
}

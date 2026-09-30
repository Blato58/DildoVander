#include "WandPresentation.h"

namespace {

constexpr uint64_t RaiseTimeoutMs = 3000;
constexpr uint64_t ShowDurationMs = 5000;
constexpr uint64_t LowerTimeoutMs = 3000;

bool Elapsed(uint64_t nowMs, uint64_t startedAtMs, uint64_t durationMs) {
  return nowMs - startedAtMs >= durationMs;
}

}  // namespace

WandPresentation::WandPresentation(WandPresentationPort& port) : port_(port) {}

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
      preview.proposalId.empty() || preview.proposalId == lastProposalId_ ||
      !port_.IsReady(preview.compartment) ||
      !port_.IsSafeToContinue() ||
      !port_.IsResting(preview.compartment)) {
    return false;
  }
  TurnProposal proposal;
  ConversationRuntimeSnapshot snapshot;
  if (!pending.Consume(proposal, snapshot, runtime, nowMs) ||
      proposal.proposalId != preview.proposalId ||
      !runtime.Matches(snapshot) ||
      (snapshot.readyMask & (1u << (proposal.compartment - 1))) == 0 ||
      proposal.catalogVersion != snapshot.catalogVersion ||
      proposal.generation != snapshot.generation ||
      proposal.deviceRevision != snapshot.deviceRevision) {
    return false;
  }
  lastProposalId_ = proposal.proposalId;
  compartment_ = proposal.compartment;
  phaseStartedAtMs_ = nowMs;
  if (!port_.Raise(compartment_, proposal.proposalId, snapshot)) {
    Fault();
    return false;
  }
  state_ = WandPresentationState::Raising;
  return true;
}

void WandPresentation::Tick(uint64_t nowMs) {
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
      if (Elapsed(nowMs, phaseStartedAtMs_, RaiseTimeoutMs)) {
        Fault();
      } else if (port_.IsRaised(compartment_)) {
        state_ = WandPresentationState::Showing;
        phaseStartedAtMs_ = nowMs;
      }
      return;
    case WandPresentationState::Showing:
      if (Elapsed(nowMs, phaseStartedAtMs_, ShowDurationMs)) {
        if (!port_.Lower(compartment_)) {
          Fault();
          return;
        }
        state_ = WandPresentationState::Lowering;
        phaseStartedAtMs_ = nowMs;
      }
      return;
    case WandPresentationState::Lowering:
      if (Elapsed(nowMs, phaseStartedAtMs_, LowerTimeoutMs)) {
        Fault();
      } else if (port_.IsResting(compartment_)) {
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

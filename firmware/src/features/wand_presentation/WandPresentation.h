#pragma once

#include <stdint.h>

#include <string>

#include "../conversation/PendingProposal.h"

enum class WandAdmissionProgress : uint8_t { Pending, Succeeded, Rejected };

// Readiness gates calibration and locally inspected/commanded position.
// PhysicalPresentation uses timed calibrated moves, not measured feedback.
// Journal admission precedes Raise; completion persists restored availability.
class WandPresentationPort {
 public:
  virtual ~WandPresentationPort() = default;
  virtual void ObserveOwnerTime(uint64_t) {}
  virtual bool AdmissionWasDuplicateOrConflict() const { return false; }
  // Control owner only: capture with a new intent immediately before admission.
  virtual uint64_t CurrentOperationFingerprint() const { return 0; }
  virtual uint64_t CurrentCalibrationRevision() const { return 0; }
  virtual bool ReserveOperation(uint8_t, const std::string&,
                                const ConversationRuntimeSnapshot&) { return false; }
  virtual void CancelOperationReservation() {}
  virtual WandAdmissionProgress ReserveClaimedOperation(
      const PendingProposal::ClaimedProposal& claim, uint64_t, uint64_t,
      uint64_t) {
    return ReserveOperation(claim.Proposal().compartment,
        claim.Proposal().proposalId, claim.Snapshot())
        ? WandAdmissionProgress::Succeeded : WandAdmissionProgress::Rejected;
  }
  virtual WandAdmissionProgress BeginClaimedOperation(
      const PendingProposal::ClaimedProposal& claim, uint64_t, uint64_t,
      uint64_t, uint64_t nowMs) {
    ObserveOwnerTime(nowMs);
    return Raise(claim.Proposal().compartment, claim.Proposal().proposalId,
        claim.Snapshot()) ? WandAdmissionProgress::Succeeded
                         : WandAdmissionProgress::Rejected;
  }
  virtual bool ValidateClaimedOperation(const PendingProposal::ClaimedProposal&,
      uint64_t, uint64_t, uint64_t) const { return true; }
  virtual void CancelClaimedOperation() { CancelOperationReservation(); }
  virtual bool IsReady(uint8_t compartment) const = 0;
  virtual bool IsSafeToContinue() const = 0;
  virtual uint64_t RaiseTimeoutMs(uint8_t) const { return 3000; }
  virtual uint64_t LowerTimeoutMs(uint8_t) const { return 3000; }
  virtual bool IsRaised(uint8_t compartment) const = 0;
  virtual bool IsResting(uint8_t compartment) const = 0;
  virtual bool Raise(uint8_t compartment, const std::string& proposalId,
                     const ConversationRuntimeSnapshot& snapshot) = 0;
  virtual bool Lower(uint8_t compartment) = 0;
  virtual bool Complete(uint8_t compartment) = 0;
  // A false Complete result is a wait only when the owner retained the exact
  // close/proof transaction. Waiting never permits additional physical effects.
  virtual bool CompletionIsPending() const { return false; }
  virtual bool CompletionTimingValidated() const { return false; }
  virtual void DisableAndFault() = 0;
};

enum class WandPresentationState : uint8_t {
  Idle,
  AwaitingAdmission,
  Raising,
  Showing,
  Lowering,
  Completing,
  Fault,
};

// An autonomous cycle for a prop that stays in its compartment. The port
// reports commanded end positions; physical verification stays unavailable.
class WandPresentation final {
 public:
  static constexpr uint64_t MaximumDurationMs = 15000;
  explicit WandPresentation(WandPresentationPort& port);

  bool Start(PendingProposal& pending,
             const ConversationRuntimeState& runtime,
             uint64_t nowMs);
  // A producer with independent cancellation/source gates polls Begin itself.
  // Tick cannot consume its admission ACK before those gates are checked again.
  bool StartPolled(PendingProposal& pending,
      const ConversationRuntimeState& runtime, uint64_t nowMs);
  // Control owner only. Pin the exact scoped operation before its producer
  // commits evidence. This claims the original proposal without physical motion.
  bool ReservePendingOperation(PendingProposal& pending,
      const ConversationRuntimeState& runtime, uint64_t nowMs);
  void CancelPendingReservation();
  bool AdmissionIsPending() const { return admissionPending_; }
  bool ReadClaimedProposal(TurnProposal& proposal) const;
  uint64_t ClaimedOperationFingerprint() const { return claimedFingerprint_; }
  uint64_t ClaimedCalibrationRevision() const { return claimedCalibrationRevision_; }
  void Tick(uint64_t nowMs);
  // Fault-only guard for the control loop before physical outputs are serviced.
  void CheckDeadline(uint64_t nowMs);
  void Stop();
  bool RecoverAfterInspection(bool inspected);
  WandPresentationState State() const;
  // Owner-only; callers must retain the captured value with the new intent.
  uint64_t CurrentOperationFingerprint() const;
  uint8_t Compartment() const;

 private:
  bool CaptureClaim(PendingProposal& pending,
      const ConversationRuntimeState& runtime, uint64_t nowMs);
  bool AdvanceReservation(uint64_t nowMs);
  bool AdvanceBegin(uint64_t nowMs);
  bool RequestBegin(PendingProposal& pending, const ConversationRuntimeState& runtime,
      uint64_t nowMs, bool automaticContinuation);
  void ClearAdmission();
  void Fault();

  WandPresentationPort& port_;
  WandPresentationState state_ = WandPresentationState::Idle;
  uint8_t compartment_ = 0;
  uint64_t phaseStartedAtMs_ = 0;
  // Admission time stays unchanged across every phase of one presentation.
  uint64_t presentationStartedAtMs_ = 0;
  uint64_t lastObservedAtMs_ = 0;
  std::string lastProposalId_;
  uint64_t lastProposalEpoch_ = 0;
  PendingProposal::ClaimedProposal claimed_;
  uint64_t claimedFingerprint_ = 0;
  uint64_t claimedCalibrationRevision_ = 0;
  bool admissionPending_ = false;
  bool reservationReady_ = false;
  bool beginRequested_ = false;
  bool beginAttempted_ = false;
  bool automaticContinuation_ = true;
};

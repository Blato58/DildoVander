#pragma once

#include <stdint.h>

#include <string>

#include "../conversation/PendingProposal.h"

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
  virtual void DisableAndFault() = 0;
};

enum class WandPresentationState : uint8_t {
  Idle,
  Raising,
  Showing,
  Lowering,
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
};

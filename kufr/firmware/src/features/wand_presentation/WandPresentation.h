#pragma once

#include <stdint.h>

#include <string>

#include "../conversation/PendingProposal.h"

// IsReady gates calibration, switched power, watchdog and known rest position.
// IsSafeToContinue checks inhibit, Stop/fault and ownership/generation after
// Raise changes the published snapshot. Raise must journal and invalidate it;
// Complete must persist restoration and republish readiness only after rest
// position is established. No such hardware adapter currently exists.
class WandPresentationPort {
 public:
  virtual ~WandPresentationPort() = default;
  virtual bool IsReady(uint8_t compartment) const = 0;
  virtual bool IsSafeToContinue() const = 0;
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
// confirms both end positions; elapsed time alone never proves restoration.
class WandPresentation final {
 public:
  explicit WandPresentation(WandPresentationPort& port);

  bool Start(PendingProposal& pending,
             const ConversationRuntimeState& runtime,
             uint64_t nowMs);
  void Tick(uint64_t nowMs);
  void Stop();
  bool RecoverAfterInspection(bool inspected);
  WandPresentationState State() const;
  uint8_t Compartment() const;

 private:
  void Fault();

  WandPresentationPort& port_;
  WandPresentationState state_ = WandPresentationState::Idle;
  uint8_t compartment_ = 0;
  uint64_t phaseStartedAtMs_ = 0;
  std::string lastProposalId_;
};

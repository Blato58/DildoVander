#include "PhysicalPresentation.h"

bool PhysicalPresentation::Raise(uint8_t compartment, const std::string& proposalId,
                                  const ConversationRuntimeSnapshot& snapshot) {
  if (phase_ != Phase::Idle || !effects_.Ready(compartment) ||
      !effects_.IsResting(compartment) ||
      !runtime_.BeginAutonomousPresentation(proposalId, compartment, snapshot)) return false;
  compartment_ = compartment;
  search_ = 1;
  pulses_ = 0;
  phase_ = Phase::Search;
  phaseAt_ = nowMs_;
  return effects_.Highlight(search_, 100, 40, 255);
}

void PhysicalPresentation::Tick(uint64_t nowMs) {
  nowMs_ = nowMs;
  if (phase_ == Phase::Idle) return;
  if (!effects_.Healthy() || nowMs < phaseAt_) { DisableAndFault(); return; }
  const uint64_t elapsed = nowMs - phaseAt_;
  switch (phase_) {
    case Phase::Idle: return;
    case Phase::Search:
      if (elapsed < 100) return;
      if (++search_ <= 6) {
        if (!effects_.Highlight(search_, 100, 40, 255)) DisableAndFault();
      } else {
        phase_ = Phase::Highlight;
        if (!effects_.Highlight(compartment_, 255, 170, 30)) DisableAndFault();
      }
      phaseAt_ = nowMs;
      return;
    case Phase::Highlight:
      if (elapsed < 400) return;
      if (!effects_.StartVibration(compartment_)) { DisableAndFault(); return; }
      phase_ = Phase::Vibration;
      phaseAt_ = nowMs;
      return;
    case Phase::Vibration:
      if (elapsed < 150) return;
      effects_.Disable();
      ++pulses_;
      phase_ = Phase::Gap;
      phaseAt_ = nowMs;
      return;
    case Phase::Gap:
      if (elapsed < 150) return;
      if (pulses_ < 3) {
        if (!effects_.StartVibration(compartment_)) { DisableAndFault(); return; }
        phase_ = Phase::Vibration;
      } else {
        if (!effects_.Move(compartment_, true)) { DisableAndFault(); return; }
        phase_ = Phase::Raise;
      }
      phaseAt_ = nowMs;
      return;
    case Phase::Raise:
      if (effects_.IsRaised(compartment_)) phase_ = Phase::Raised;
      return;
    case Phase::Raised:
      if (!effects_.IsRaised(compartment_)) DisableAndFault();
      return;
    case Phase::Lower:
      return;
  }
}

bool PhysicalPresentation::IsRaised(uint8_t compartment) const {
  return compartment == compartment_ && phase_ == Phase::Raised && effects_.IsRaised(compartment);
}
bool PhysicalPresentation::IsResting(uint8_t compartment) const {
  return (phase_ == Phase::Idle || (phase_ == Phase::Lower && compartment == compartment_)) &&
         effects_.IsResting(compartment);
}
bool PhysicalPresentation::Lower(uint8_t compartment) {
  if (!IsRaised(compartment) || !effects_.Move(compartment, false)) return false;
  phase_ = Phase::Lower;
  return true;
}
bool PhysicalPresentation::Complete(uint8_t compartment) {
  if (!IsResting(compartment)) return false;
  effects_.Disable();
  if (!effects_.Healthy() || !runtime_.CompleteAutonomousPresentation(compartment)) return false;
  phase_ = Phase::Idle;
  compartment_ = 0;
  return effects_.Highlight(0, 0, 0, 0);
}
void PhysicalPresentation::DisableAndFault() {
  effects_.Fault();
  effects_.Highlight(0, 0, 0, 0);
  phase_ = Phase::Idle;
  runtime_.FaultAutonomousPresentation();
}

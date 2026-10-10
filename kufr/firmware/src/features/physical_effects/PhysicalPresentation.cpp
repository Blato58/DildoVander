#include "PhysicalPresentation.h"

bool PhysicalPresentation::Raise(uint8_t compartment, const std::string& proposalId,
                                  const ConversationRuntimeSnapshot& snapshot) {
  if (phase_ != Phase::Idle || !effects_.Ready(compartment) ||
      !effects_.IsResting(compartment) ||
      !runtime_.BeginAutonomousPresentation(proposalId, compartment, snapshot)) return false;
  compartment_ = compartment;
  scoreIndex_ = 0;
  phase_ = Phase::Score;
  phaseAt_ = nowMs_;
  if (!EnterScoreStep()) { DisableAndFault(); return false; }
  return true;
}

unsigned PhysicalPresentation::StepCount() const {
  return repeat_ ? sizeof(PresentationScore::Short) / sizeof(PresentationScore::Step)
                 : sizeof(PresentationScore::Main) / sizeof(PresentationScore::Step);
}

const PresentationScore::Step& PhysicalPresentation::CurrentStep() const {
  return repeat_ ? PresentationScore::Short[scoreIndex_] : PresentationScore::Main[scoreIndex_];
}

uint16_t PhysicalPresentation::RestDurationMs() const {
  return repeat_ ? PresentationScore::Short[StepCount() - 1].durationMs
                 : PresentationScore::Main[StepCount() - 1].durationMs;
}

bool PhysicalPresentation::EnterScoreStep() {
  const auto& step = CurrentStep();
  uint8_t target = compartment_;
  if (step.target > 0) target = static_cast<uint8_t>(step.target);
  else if (step.target == -2) target = compartment_ % 6 + 1;
  else if (step.target == -3) target = (compartment_ + 4) % 6 + 1;
  const uint8_t mask = step.target == -1 ? 63 : static_cast<uint8_t>(1u << (target - 1));
  if (!effects_.Illuminate(mask, step.red, step.green, step.blue)) return false;
  if (step.vibration && !effects_.StartVibration(target)) return false;
  if (step.stage == PresentationScore::Stage::Raise ||
      step.stage == PresentationScore::Stage::Lower) {
    if (!effects_.Move(compartment_, step.stage == PresentationScore::Stage::Raise)) return false;
  }
  if (audio_ && step.toneHz) {
    audio_->Cue(step.toneHz, step.stage == PresentationScore::Stage::Raise ? 180 : 100);
  }
  if (audio_ && step.stage == PresentationScore::Stage::Rest) audio_->StopCues();
  return true;
}

void PhysicalPresentation::Tick(uint64_t nowMs) {
  nowMs_ = nowMs;
  if (phase_ == Phase::Idle) return;
  if (!effects_.Healthy() || nowMs < phaseAt_) { DisableAndFault(); return; }
  switch (phase_) {
    case Phase::Idle: return;
    case Phase::Score:
      if (nowMs - phaseAt_ < CurrentStep().durationMs) return;
      effects_.Disable();
      if (!effects_.Healthy()) { DisableAndFault(); return; }
      ++scoreIndex_;
      if (!EnterScoreStep()) { DisableAndFault(); return; }
      if (CurrentStep().stage == PresentationScore::Stage::Raise) {
        phase_ = Phase::Raise;
      }
      phaseAt_ = nowMs;
      return;
    case Phase::Raise:
      if (effects_.IsRaised(compartment_)) {
        ++scoreIndex_;
        if (!EnterScoreStep()) { DisableAndFault(); return; }
        phase_ = Phase::Raised;
      }
      return;
    case Phase::Raised:
      if (!effects_.IsRaised(compartment_)) DisableAndFault();
      return;
    case Phase::Lower:
      if (effects_.IsResting(compartment_)) {
        effects_.Disable();
        ++scoreIndex_;
        if (!effects_.Healthy() || !EnterScoreStep()) { DisableAndFault(); return; }
        phase_ = Phase::Rest;
        phaseAt_ = nowMs;
      }
      return;
    case Phase::Rest:
      if (!effects_.IsResting(compartment_)) DisableAndFault();
      return;
  }
}

bool PhysicalPresentation::IsRaised(uint8_t compartment) const {
  return compartment == compartment_ && phase_ == Phase::Raised && effects_.IsRaised(compartment);
}
bool PhysicalPresentation::IsResting(uint8_t compartment) const {
  return (phase_ == Phase::Idle || (phase_ == Phase::Rest && compartment == compartment_ &&
          nowMs_ - phaseAt_ >= CurrentStep().durationMs)) &&
         effects_.IsResting(compartment);
}
bool PhysicalPresentation::Lower(uint8_t compartment) {
  if (!IsRaised(compartment)) return false;
  ++scoreIndex_;
  if (!EnterScoreStep()) { DisableAndFault(); return false; }
  phase_ = Phase::Lower;
  return true;
}
bool PhysicalPresentation::Complete(uint8_t compartment) {
  if (!IsResting(compartment)) return false;
  effects_.Disable();
  if (!effects_.Healthy() || !runtime_.CompleteAutonomousPresentation(compartment)) return false;
  phase_ = Phase::Idle;
  repeat_ = true;
  compartment_ = 0;
  return effects_.Highlight(0, 0, 0, 0);
}
void PhysicalPresentation::DisableAndFault() {
  if (audio_) audio_->StopCues();
  effects_.Fault();
  effects_.Highlight(0, 0, 0, 0);
  phase_ = Phase::Idle;
  runtime_.FaultAutonomousPresentation();
}

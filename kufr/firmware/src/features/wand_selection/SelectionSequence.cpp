#include "SelectionSequence.h"

namespace {

constexpr uint32_t SearchStepDurationMs = 100;
constexpr uint32_t HighlightDurationMs = 400;
constexpr uint32_t VibrationOnDurationMs = 150;
constexpr uint32_t VibrationOffDurationMs = 150;
constexpr uint32_t LatchReleaseDurationMs = 400;
constexpr uint32_t VibrationHardLimitMs = 250;
constexpr uint32_t LatchHardLimitMs = 500;
constexpr uint32_t SequenceHardLimitMs = 5000;
constexpr uint8_t VibrationPulseCount = 3;

}  // namespace

SelectionSequence::SelectionSequence(SafetyController& safety) : safety_(safety) {}

bool SelectionSequence::Start(uint8_t compartment, uint32_t nowMs,
                              SelectionStep& step) {
  if (IsBusy() || compartment < 1 || compartment > CompartmentCount ||
      !safety_.BeginSelection(compartment)) {
    return false;
  }

  selectedCompartment_ = compartment;
  searchStep_ = 1;
  completedPulses_ = 0;
  sequenceStartedAtMs_ = nowMs;
  phaseStartedAtMs_ = nowMs;
  phase_ = Phase::Searching;
  step = {SelectionEffect::SearchStep, searchStep_};
  return true;
}

bool SelectionSequence::Update(uint32_t nowMs, SelectionStep& step) {
  if (!IsBusy()) {
    return false;
  }

  if (safety_.IsStopped()) {
    Stop(step);
    return true;
  }

  // Unsigned subtraction keeps short delays correct across millis() wraparound.
  if (nowMs - sequenceStartedAtMs_ >= SequenceHardLimitMs) {
    Stop(step);
    return true;
  }

  const uint32_t elapsedMs = nowMs - phaseStartedAtMs_;

  switch (phase_) {
    case Phase::Idle:
      return false;

    case Phase::Searching:
      if (elapsedMs < SearchStepDurationMs) {
        return false;
      }

      phaseStartedAtMs_ = nowMs;
      if (searchStep_ < CompartmentCount) {
        ++searchStep_;
        step = {SelectionEffect::SearchStep, searchStep_};
      } else {
        phase_ = Phase::Highlighting;
        step = {SelectionEffect::Highlight, selectedCompartment_};
      }
      return true;

    case Phase::Highlighting:
      if (elapsedMs < HighlightDurationMs) {
        return false;
      }

      phaseStartedAtMs_ = nowMs;
      if (!safety_.StartVibration()) {
        Stop(step);
        return true;
      }
      phase_ = Phase::Vibrating;
      step = {SelectionEffect::VibrationOn, selectedCompartment_};
      return true;

    case Phase::Vibrating:
      if (elapsedMs >= VibrationHardLimitMs) {
        Stop(step);
        return true;
      }

      if (elapsedMs < VibrationOnDurationMs) {
        return false;
      }

      safety_.DisableActuators();
      phaseStartedAtMs_ = nowMs;
      phase_ = Phase::BetweenPulses;
      ++completedPulses_;
      step = {SelectionEffect::VibrationOff, selectedCompartment_};
      return true;

    case Phase::BetweenPulses:
      if (elapsedMs < VibrationOffDurationMs) {
        return false;
      }

      phaseStartedAtMs_ = nowMs;
      if (completedPulses_ < VibrationPulseCount) {
        if (!safety_.StartVibration()) {
          Stop(step);
          return true;
        }
        phase_ = Phase::Vibrating;
        step = {SelectionEffect::VibrationOn, selectedCompartment_};
      } else {
        if (!safety_.ReleaseLatch()) {
          Stop(step);
          return true;
        }
        phase_ = Phase::Releasing;
        step = {SelectionEffect::ReleaseLatch, selectedCompartment_};
      }
      return true;

    case Phase::Releasing:
      if (elapsedMs >= LatchHardLimitMs) {
        Stop(step);
        return true;
      }

      if (elapsedMs < LatchReleaseDurationMs) {
        return false;
      }

      safety_.Complete();
      phase_ = Phase::Idle;
      selectedCompartment_ = 0;
      step = {SelectionEffect::Idle, 0};
      return true;
  }

  return false;
}

bool SelectionSequence::Stop(SelectionStep& step) {
  const bool newlyStopped = safety_.Stop();
  phase_ = Phase::Idle;
  step = {SelectionEffect::Stopped, selectedCompartment_};
  return newlyStopped;
}

bool SelectionSequence::RecoverAfterInspection() {
  if (IsBusy() || !safety_.RecoverAfterInspection()) {
    return false;
  }

  selectedCompartment_ = 0;
  return true;
}

bool SelectionSequence::IsBusy() const { return phase_ != Phase::Idle; }

bool SelectionSequence::NeedsRecovery() const {
  return safety_.NeedsRecovery();
}

uint8_t SelectionSequence::SelectedCompartment() const {
  return selectedCompartment_;
}

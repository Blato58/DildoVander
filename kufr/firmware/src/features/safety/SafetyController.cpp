#include "SafetyController.h"

SafetyController::SafetyController(SafetyPort& port) : port_(port) {}

void SafetyController::InitializeSafe() { port_.DisableActuators(); }

bool SafetyController::RecoverAfterInspection() {
  if (state_ == State::Running) {
    return false;
  }

  port_.DisableActuators();
  output_ = Output::Off;
  faultLatched_ = false;
  compartment_ = 0;
  state_ = State::Ready;
  return true;
}

bool SafetyController::BeginSelection(uint8_t compartment) {
  if (state_ != State::Ready || compartment < 1 || compartment > 6) {
    return false;
  }

  port_.DisableActuators();
  output_ = Output::Off;
  compartment_ = compartment;
  state_ = State::Running;
  return true;
}

bool SafetyController::StartVibration() {
  if (state_ != State::Running || output_ != Output::Off) {
    return false;
  }

  port_.StartVibration(compartment_);
  output_ = Output::Vibration;
  return true;
}

bool SafetyController::ReleaseLatch() {
  if (state_ != State::Running || output_ != Output::Off) {
    return false;
  }

  port_.ReleaseLatch(compartment_);
  output_ = Output::Latch;
  return true;
}

void SafetyController::DisableActuators() {
  port_.DisableActuators();
  output_ = Output::Off;
}

bool SafetyController::LowerProp() {
  if (state_ != State::Running || output_ != Output::Off) return false;
  port_.LowerProp(compartment_);
  output_ = Output::Latch;
  return true;
}

void SafetyController::Complete() {
  DisableActuators();
  if (state_ == State::Running) {
    compartment_ = 0;
    state_ = State::Ready;
  }
}

bool SafetyController::Stop() {
  // Repeat the off request even if Stop was already latched.
  DisableActuators();
  const bool newlyStopped = state_ != State::Stopped;
  state_ = State::Stopped;
  return newlyStopped;
}

bool SafetyController::ReportFault() {
  DisableActuators();
  const bool newlyFaulted = !faultLatched_;
  faultLatched_ = true;
  if (state_ != State::Stopped) {
    state_ = State::Fault;
  }

  return newlyFaulted;
}

bool SafetyController::IsStopped() const {
  return state_ == State::Stopped || state_ == State::Fault;
}

bool SafetyController::IsFaulted() const { return faultLatched_; }

bool SafetyController::NeedsRecovery() const {
  return state_ == State::RecoveryRequired || state_ == State::Stopped ||
         state_ == State::Fault;
}

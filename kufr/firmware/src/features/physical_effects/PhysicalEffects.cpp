#include "PhysicalEffects.h"

bool PhysicalEffects::Configure(const std::array<ServoCalibration, 6>& calibration,
                                uint64_t revision, bool inhibitVerified) {
  Disable();
  configured_ = false;
  if (!inhibitVerified || revision == 0) return false;
  for (const auto& servo : calibration) {
    if (servo.restUs < 500 || servo.restUs > 2500 || servo.raisedUs < 500 ||
        servo.raisedUs > 2500 || servo.restUs == servo.raisedUs ||
        servo.travelMs < 20 || servo.travelMs > 500) return false;
  }
  calibration_ = calibration;
  revision_ = revision;
  configured_ = true;
  fault_ = true;  // Every boot still needs a deliberate local inspection.
  return true;
}

bool PhysicalEffects::SafeInputs() const {
  return inputs_.enabled && inputs_.watchdogGood &&
         (inputs_.restingMask & inputs_.raisedMask) == 0;
}

void PhysicalEffects::Tick(uint64_t nowMs) {
  nowMs_ = nowMs;
  if (observed_ && (nowMs < lastPass_ || nowMs - lastPass_ >= 250)) Fault();
  observed_ = true;
  lastPass_ = nowMs;
  if (!configured_) return;
  PhysicalInputs inputs;
  if (!io_.ReadInputs(inputs)) { Fault(); return; }
  if ((inputs.restingMask & inputs.raisedMask) != 0) { Fault(); return; }
  if (candidateRest_ != inputs.restingMask || candidateRaised_ != inputs.raisedMask) {
    candidateRest_ = inputs.restingMask;
    candidateRaised_ = inputs.raisedMask;
    positionChangedAt_ = nowMs;
    // Withdrawal is immediate; only assertion needs stable 30ms feedback.
    inputs_.restingMask &= inputs.restingMask;
    inputs_.raisedMask &= inputs.raisedMask;
  }
  inputs_.enabled = inputs.enabled;
  inputs_.watchdogGood = inputs.watchdogGood;
  inputs_.button = inputs.button;
  if (nowMs - positionChangedAt_ >= 30) {
    inputs_.restingMask = candidateRest_;
    inputs_.raisedMask = candidateRaised_;
  }
  if (!SafeInputs()) Fault();
  // Heartbeat is produced only by completed passes, including inhibited
  // startup passes so GOOD can settle before inspection. No free-running PWM.
  if (nowMs < heartbeatAt_ || nowMs - heartbeatAt_ >= 50) {
    io_.Heartbeat(nowMs);
    heartbeatAt_ = nowMs;
  }
  if (!active_) return;
  const uint64_t elapsed = nowMs - activeAt_;
  if (!servo_) {
    if (elapsed >= 150) Disable();
  } else if (Position(active_, raising_)) {
    Disable();
  } else if (elapsed >= calibration_[active_ - 1].travelMs) {
    Fault();  // Time never substitutes for measured endpoint feedback.
  }
}

void PhysicalEffects::Disable() {
  io_.RunRequest(false);
  active_ = 0;
  servo_ = false;
  if (!io_.FullOff()) fault_ = true;
}

void PhysicalEffects::Fault() {
  fault_ = true;
  Disable();
}

bool PhysicalEffects::RecoverAfterInspection() {
  if (!configured_ || !observed_ || !SafeInputs() || inputs_.restingMask != 63 || active_)
    return false;
  Disable();
  if (!io_.FullOff()) return false;
  fault_ = false;
  return true;
}

bool PhysicalEffects::Highlight(uint8_t compartment, uint8_t red, uint8_t green, uint8_t blue) {
  if (compartment > 6) return false;
  std::array<uint8_t, 108> pixels{};
  if (compartment) {
    const unsigned start = (compartment - 1) * 18;
    for (unsigned i = start; i < start + 18; i += 3) {
      pixels[i] = static_cast<uint8_t>(green * KufrBoard::InitialBrightness / 255);
      pixels[i + 1] = static_cast<uint8_t>(red * KufrBoard::InitialBrightness / 255);
      pixels[i + 2] = static_cast<uint8_t>(blue * KufrBoard::InitialBrightness / 255);
    }
  }
  if (!io_.Lights(pixels)) { Fault(); return false; }
  return true;
}

bool PhysicalEffects::Ready(uint8_t compartment) const {
  return Healthy() && SafeInputs() && compartment >= 1 && compartment <= 6 && !active_;
}

bool PhysicalEffects::StartVibration(uint8_t compartment) {
  if (!Ready(compartment) || !IsResting(compartment)) return false;
  io_.RunRequest(false);
  if (!io_.FullOff() || !io_.Vibration(compartment + 5, true)) { Fault(); return false; }
  active_ = compartment;
  activeAt_ = nowMs_;
  servo_ = false;
  io_.RunRequest(true);
  return true;
}

bool PhysicalEffects::Move(uint8_t compartment, bool raise) {
  if (!Ready(compartment)) return false;
  // Start must leave a confirmed source endpoint; never seek home blindly.
  if (!Position(compartment, !raise) || Position(compartment, raise)) return false;
  io_.RunRequest(false);
  const auto& calibration = calibration_[compartment - 1];
  if (!io_.FullOff() || !io_.Servo(compartment - 1, raise ? calibration.raisedUs : calibration.restUs)) {
    Fault(); return false;
  }
  active_ = compartment;
  activeAt_ = nowMs_;
  servo_ = true;
  raising_ = raise;
  io_.RunRequest(true);
  return true;
}

bool PhysicalEffects::Position(uint8_t compartment, bool raise) const {
  if (compartment < 1 || compartment > 6) return false;
  const auto mask = raise ? inputs_.raisedMask : inputs_.restingMask;
  return (mask & (1u << (compartment - 1))) != 0;
}
bool PhysicalEffects::IsRaised(uint8_t compartment) const { return Healthy() && Position(compartment, true); }
bool PhysicalEffects::IsResting(uint8_t compartment) const { return Healthy() && Position(compartment, false); }

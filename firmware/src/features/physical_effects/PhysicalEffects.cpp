#include "PhysicalEffects.h"

bool PhysicalEffects::Configure(const std::array<ServoCalibration, 6>& calibration,
                                uint64_t revision) {
  Disable();
  configured_ = false;
  if (revision == 0) return false;
  for (const auto& servo : calibration) {
    if (servo.restUs < 500 || servo.restUs > 2500 || servo.raisedUs < 500 ||
        servo.raisedUs > 2500 || servo.restUs == servo.raisedUs ||
        servo.travelMs < 20 || servo.travelMs > 3000) return false;
  }
  calibration_ = calibration;
  revision_ = revision;
  configured_ = true;
  inspectionRequired_ = true;  // Every boot still needs a deliberate local inspection.
  return true;
}

void PhysicalEffects::Tick(uint64_t nowMs) {
  nowMs_ = nowMs;
  if (observed_ && (nowMs < lastPass_ || nowMs - lastPass_ >= 250)) Fault();
  observed_ = true;
  lastPass_ = nowMs;
  PhysicalInputs inputs;
  if (!io_.ReadInputs(inputs)) { Fault(); return; }
  inputs_ = inputs;
  if (!active_) return;
  const uint64_t elapsed = nowMs - activeAt_;
  if (!servo_) {
    if (elapsed >= 250) Fault();
    else if (elapsed >= VibrationDurationMs) Disable();
  } else if (elapsed >= MotionTimeoutMs(active_)) {
    Fault();
  } else if (elapsed >= calibration_[active_ - 1].travelMs) {
    // No return channel on these servos: this is commanded position only.
    const uint8_t bit = static_cast<uint8_t>(1u << (active_ - 1));
    if (raising_) raisedMask_ |= bit;
    else restingMask_ |= bit;
    Disable();
  }
}

void PhysicalEffects::Disable() {
  io_.EnableOutputs(false);
  active_ = 0;
  servo_ = false;
  if (!io_.FullOff()) fault_ = true;
}

void PhysicalEffects::Fault() {
  io_.RequestUrgentStop();
  fault_ = true;
  Disable();
}

bool PhysicalEffects::RecoverAfterInspection() {
  if (!configured_ || !observed_ || active_)
    return false;
  const auto inspectedStopEpoch = io_.StopEpoch();
  Disable();
  if (!io_.FullOff()) { fault_ = true; return false; }
  if (!io_.ClearUrgentStopAfterInspection(inspectedStopEpoch)) { fault_ = true; return false; }
  // The local inspection confirms that all trays were manually put at rest.
  restingMask_ = 63;
  raisedMask_ = 0;
  fault_ = false;
  inspectionRequired_ = false;
  return true;
}

bool PhysicalEffects::Highlight(uint8_t compartment, uint8_t red, uint8_t green, uint8_t blue) {
  if (compartment > 6) return false;
  return Illuminate(compartment ? static_cast<uint8_t>(1u << (compartment - 1)) : 0,
                    red, green, blue);
}

bool PhysicalEffects::Illuminate(uint8_t mask, uint8_t red, uint8_t green, uint8_t blue) {
  return IlluminateAtLevel(mask, red, green, blue, KufrBoard::InitialBrightness);
}

bool PhysicalEffects::IlluminateAtLevel(uint8_t mask, uint8_t red, uint8_t green,
                                      uint8_t blue, uint8_t level) {
  // The power contract permits one six-LED segment, including during motion.
  // Enforce it here for every caller, not only generated presentation scores.
  if (mask > 63 || (mask & (mask - 1)) != 0 || level > KufrBoard::InitialBrightness) return false;
  std::array<uint8_t, 108> pixels{};
  for (uint8_t compartment = 1; compartment <= 6; ++compartment) {
    if (!(mask & (1u << (compartment - 1)))) continue;
    const unsigned start = (compartment - 1) * 18;
    for (unsigned i = start; i < start + 18; i += 3) {
      pixels[i] = static_cast<uint8_t>(green * level / 255);
      pixels[i + 1] = static_cast<uint8_t>(red * level / 255);
      pixels[i + 2] = static_cast<uint8_t>(blue * level / 255);
    }
  }
  if (!io_.Lights(pixels)) { Fault(); return false; }
  return true;
}

bool PhysicalEffects::Ready(uint8_t compartment) const {
  return Healthy() && compartment >= 1 && compartment <= 6 && !active_;
}

bool PhysicalEffects::StartVibration(uint8_t compartment) {
  if (!Ready(compartment) || !IsResting(compartment)) return false;
  io_.EnableOutputs(false);
  if (!io_.FullOff() || !io_.Vibration(compartment + 5, true)) { Fault(); return false; }
  active_ = compartment;
  activeAt_ = nowMs_;
  servo_ = false;
  io_.EnableOutputs(true);
  return true;
}

bool PhysicalEffects::Move(uint8_t compartment, bool raise) {
  if (!Ready(compartment)) return false;
  // Only move from a known commanded source; recovery requires local inspection.
  if (!Position(compartment, !raise) || Position(compartment, raise)) return false;
  io_.EnableOutputs(false);
  const auto& calibration = calibration_[compartment - 1];
  if (!io_.FullOff() || !io_.Servo(compartment - 1, raise ? calibration.raisedUs : calibration.restUs)) {
    Fault(); return false;
  }
  active_ = compartment;
  activeAt_ = nowMs_;
  servo_ = true;
  raising_ = raise;
  const uint8_t bit = static_cast<uint8_t>(1u << (compartment - 1));
  restingMask_ &= ~bit;
  raisedMask_ &= ~bit;
  io_.EnableOutputs(true);
  return true;
}

bool PhysicalEffects::Position(uint8_t compartment, bool raise) const {
  if (compartment < 1 || compartment > 6) return false;
  const auto mask = raise ? raisedMask_ : restingMask_;
  return (mask & (1u << (compartment - 1))) != 0;
}
bool PhysicalEffects::IsRaised(uint8_t compartment) const { return Healthy() && Position(compartment, true); }
bool PhysicalEffects::IsResting(uint8_t compartment) const { return Healthy() && Position(compartment, false); }

uint32_t PhysicalEffects::MotionTimeoutMs(uint8_t compartment) const {
  if (!configured_ || compartment < 1 || compartment > 6) return 0;
  return static_cast<uint32_t>(calibration_[compartment - 1].travelMs) + 250;
}

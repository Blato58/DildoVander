#include "VirtualHardware.h"
#include <algorithm>

void VirtualHardware::Advance(uint64_t nowMs) {
  const uint64_t elapsed = nowMs - lastMs_;
  lastMs_ = nowMs;
  if (!enabled || !activeServo_ || jammed[activeServo_ - 1]) return;
  double& value = position[activeServo_ - 1];
  const double distance = static_cast<double>(elapsed) / 500.0;
  value = target_ > value ? std::min(target_, value + distance)
                          : std::max(target_, value - distance);
}

void VirtualHardware::Inspect() {
  // INSPECT is an explicit simulated operator assertion of rest/reload.
  position.fill(0);
  poseKnown = true;
}

bool VirtualHardware::FullOff() {
  orderingHealthy = orderingHealthy && !enabled;
  if (enabled) return false;
  vibrationMask = 0;
  activeServo_ = 0;
  return true;
}

bool VirtualHardware::Servo(uint8_t channel, uint16_t pulseUs) {
  orderingHealthy = orderingHealthy && !enabled && !vibrationMask;
  if (enabled || vibrationMask || channel >= 6 ||
      (fault == "servo" && (!faultSlot || faultSlot == channel + 1))) return false;
  pulse[channel] = pulseUs;
  activeServo_ = channel + 1;
  target_ = pulseUs == 1900 ? 1 : 0;
  ++servoWrites;
  return true;
}

bool VirtualHardware::Vibration(uint8_t channel, bool value) {
  orderingHealthy = orderingHealthy && !enabled && !activeServo_ && !vibrationMask;
  if (enabled || activeServo_ || vibrationMask || channel < 6 || channel > 11 || !value) return false;
  vibrationMask = static_cast<uint8_t>(1u << (channel - 6));
  ++vibrationWrites;
  return true;
}

bool VirtualHardware::Lights(const std::array<uint8_t, 108>& frame) {
  if (fault == "led") return false;
  unsigned lit = 0;
  for (unsigned start = 0; start < frame.size(); start += 18) {
    bool segment = false;
    for (unsigned offset = 0; offset < 18; ++offset) segment |= frame[start + offset] != 0;
    lit += segment ? 1 : 0;
  }
  orderingHealthy = orderingHealthy && lit <= 1;
  if (lit > 1) return false;
  led = frame;
  ++lightWrites;
  return true;
}

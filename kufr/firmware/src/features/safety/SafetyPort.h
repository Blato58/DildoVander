#pragma once

#include <stdint.h>

// Requests only. The hardware adapter disables PCA signal outputs through OE.
// That does not remove the servo supply; the service switch does.
class SafetyPort {
 public:
  virtual ~SafetyPort() {}

  virtual void DisableActuators() = 0;
  virtual void StartVibration(uint8_t compartment) = 0;
  virtual void ReleaseLatch(uint8_t compartment) = 0;
  virtual void LowerProp(uint8_t) {}
  virtual uint32_t MotionTimeoutMs(uint8_t) const { return 500; }
  virtual bool PropRaised(uint8_t) const { return false; }
  virtual bool PropResting(uint8_t) const { return false; }
};

#pragma once

#include <stdint.h>

// Requests only. A hardware adapter must implement the independent inhibit and
// watchdog; calling this port alone does not prove that power was removed.
class SafetyPort {
 public:
  virtual ~SafetyPort() {}

  virtual void DisableActuators() = 0;
  virtual void StartVibration(uint8_t compartment) = 0;
  virtual void ReleaseLatch(uint8_t compartment) = 0;
  virtual void LowerProp(uint8_t) {}
  virtual bool PropRaised(uint8_t) const { return false; }
  virtual bool PropResting(uint8_t) const { return false; }
};

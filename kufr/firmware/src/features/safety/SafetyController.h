#pragma once

#include <stdint.h>

#include "SafetyPort.h"

class SelectionSequence;

class SafetyController {
 public:
  explicit SafetyController(SafetyPort& port);

  void InitializeSafe();
  bool Stop();
  bool ReportFault();

  bool IsStopped() const;
  bool IsFaulted() const;
  bool NeedsRecovery() const;

 private:
  friend class SelectionSequence;

  bool RecoverAfterInspection();
  bool BeginSelection(uint8_t compartment);
  bool StartVibration();
  bool ReleaseLatch();
  void DisableActuators();
  void Complete();

  enum class State : uint8_t {
    RecoveryRequired,
    Ready,
    Running,
    Stopped,
    Fault,
  };

  enum class Output : uint8_t {
    Off,
    Vibration,
    Latch,
  };

  SafetyPort& port_;
  State state_ = State::RecoveryRequired;
  Output output_ = Output::Off;
  bool faultLatched_ = false;
  uint8_t compartment_ = 0;
};

#pragma once

#include <stdint.h>

#include "../safety/SafetyController.h"

enum class SelectionEffect : uint8_t {
  SearchStep,
  Highlight,
  VibrationOn,
  VibrationOff,
  ReleaseLatch,
  Idle,
  Stopped,
};

enum class SelectionControlMode : uint8_t {
  Standalone,
  Manual,
};

enum class SelectionPhase : uint8_t {
  Idle,
  Searching,
  Highlighting,
  Vibrating,
  Releasing,
};

struct SelectionStep {
  SelectionStep(SelectionEffect effect = SelectionEffect::Idle,
                uint8_t compartment = 0)
      : effect(effect), compartment(compartment) {}

  SelectionEffect effect;
  uint8_t compartment;
};

class SelectionSequence {
 public:
  static constexpr uint8_t CompartmentCount = 6;

  explicit SelectionSequence(SafetyController& safety, bool returnsProp = false);
  bool ReturnsProp() const { return returnsProp_; }

  bool Start(uint8_t compartment, uint32_t nowMs, SelectionStep& step);
  bool Start(uint8_t compartment, uint32_t nowMs,
             SelectionControlMode controlMode, SelectionStep& step);
  bool Update(uint32_t nowMs, SelectionStep& step);
  bool Stop(SelectionStep& step);
  bool ReportFault(SelectionStep& step);
  bool ReportManualLeaseLost(SelectionStep& step);
  bool ObserveControlLoopPass(uint32_t nowMs, SelectionStep& step);
  bool RecoverAfterInspection();

  bool IsBusy() const;
  bool NeedsRecovery() const;
  uint8_t SelectedCompartment() const;
  SelectionPhase CurrentPhase() const;

 private:
  enum class Phase : uint8_t {
    Idle,
    Searching,
    Highlighting,
    Vibrating,
    BetweenPulses,
    Releasing,
    Showing,
    Returning,
  };

  SafetyController& safety_;
  bool returnsProp_;
  Phase phase_ = Phase::Idle;
  SelectionControlMode controlMode_ = SelectionControlMode::Standalone;
  bool hasControlLoopPass_ = false;
  uint32_t lastControlLoopPassAtMs_ = 0;
  uint32_t sequenceStartedAtMs_ = 0;
  uint32_t phaseStartedAtMs_ = 0;
  uint8_t selectedCompartment_ = 0;
  uint8_t searchStep_ = 0;
  uint8_t completedPulses_ = 0;
};

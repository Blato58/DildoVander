#pragma once

#include <stdint.h>

#include "../safety/SafetyPort.h"
#include "SelectionSequence.h"

class SerialSelectionPort : public SafetyPort {
 public:
  void Begin();
  void Poll(SelectionSequence& sequence, uint32_t nowMs);
  void Emit(const SelectionStep& step);

  void DisableActuators() override;
  void StartVibration(uint8_t compartment) override;
  void ReleaseLatch(uint8_t compartment) override;

 private:
  void HandleLine(SelectionSequence& sequence, uint32_t nowMs);

  static constexpr uint8_t MaxLineLength = 16;
  char line_[MaxLineLength + 1] = {};
  uint8_t lineLength_ = 0;
  bool discardingLine_ = false;
};

#include <Arduino.h>

#include "features/safety/SafetyController.h"
#include "features/wand_selection/SelectionSequence.h"
#include "features/wand_selection/SerialSelectionPort.h"

namespace {

SerialSelectionPort serialPort;
SafetyController safetyController(serialPort);
SelectionSequence selectionSequence(safetyController);

}  // namespace

void setup() {
  Serial.begin(115200);
  safetyController.InitializeSafe();
  serialPort.Begin();
}

void loop() {
  const uint32_t nowMs = millis();

  SelectionStep step;
  if (selectionSequence.ObserveControlLoopPass(nowMs, step)) {
    serialPort.Emit(step);
    return;
  }

  serialPort.Poll(selectionSequence, nowMs);

  if (selectionSequence.Update(nowMs, step)) {
    serialPort.Emit(step);
  }
}


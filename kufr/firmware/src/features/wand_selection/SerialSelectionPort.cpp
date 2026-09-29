#include "SerialSelectionPort.h"

#include <Arduino.h>
#include <string.h>

void SerialSelectionPort::Begin() {
  Serial.println("AI kufr: serial selection prototype");
  Serial.println("Commands: select 1..6, stop, reset, status, help");
  Serial.println("Outputs are logged only; no GPIO is configured.");
  Serial.println("After inspection, type reset before selecting.");
}

void SerialSelectionPort::Poll(SelectionSequence& sequence, uint32_t nowMs) {
  // Bound work per loop so a busy serial sender cannot stall the sequence.
  uint8_t processedBytes = 0;
  while (Serial.available() > 0 && processedBytes < 32) {
    ++processedBytes;
    const char character = static_cast<char>(Serial.read());

    if (character == '\r') {
      continue;
    }

    if (character == '\n') {
      if (discardingLine_) {
        Serial.println("error: command too long");
      } else {
        line_[lineLength_] = '\0';
        HandleLine(sequence, nowMs);
      }

      lineLength_ = 0;
      discardingLine_ = false;
      continue;
    }

    if (discardingLine_) {
      continue;
    }

    if (lineLength_ >= MaxLineLength) {
      discardingLine_ = true;
      continue;
    }

    line_[lineLength_++] = character;
  }
}

void SerialSelectionPort::HandleLine(SelectionSequence& sequence,
                                     uint32_t nowMs) {
  if (strcmp(line_, "help") == 0) {
    Serial.println("Commands: select 1..6, stop, reset, status, help");
    Serial.println("reset requires operator inspection; outputs are log only.");
    return;
  }

  if (strcmp(line_, "status") == 0) {
    if (sequence.IsBusy()) {
      Serial.print("busy: compartment ");
      Serial.println(sequence.SelectedCompartment());
    } else if (sequence.NeedsRecovery()) {
      Serial.print("recovery required: last compartment ");
      Serial.println(sequence.SelectedCompartment());
    } else {
      Serial.println("ready");
    }
    return;
  }

  if (strcmp(line_, "stop") == 0) {
    SelectionStep step;
    sequence.Stop(step);
    Emit(step);
    return;
  }

  if (strcmp(line_, "reset") == 0) {
    if (sequence.RecoverAfterInspection()) {
      Serial.println("ready after operator inspection");
    } else {
      Serial.println("error: stop the active selection first");
    }
    return;
  }

  if (lineLength_ == 8 && strncmp(line_, "select ", 7) == 0 &&
      line_[7] >= '1' && line_[7] <= '6') {
    SelectionStep step;
    const uint8_t compartment = static_cast<uint8_t>(line_[7] - '0');
    if (!sequence.Start(compartment, nowMs, step)) {
      Serial.println(sequence.NeedsRecovery()
                         ? "error: inspection and reset required"
                         : "error: selection in progress");
      return;
    }

    Emit(step);
    return;
  }

  if (lineLength_ > 0) {
    Serial.println("error: unknown command; type help");
  }
}

void SerialSelectionPort::Emit(const SelectionStep& step) {
  switch (step.effect) {
    case SelectionEffect::SearchStep:
      Serial.print("search: compartment ");
      break;
    case SelectionEffect::Highlight:
      Serial.print("highlight: compartment ");
      break;
    case SelectionEffect::VibrationOn:
      Serial.print("vibration on: compartment ");
      break;
    case SelectionEffect::VibrationOff:
      Serial.print("vibration off: compartment ");
      break;
    case SelectionEffect::ReleaseLatch:
      Serial.print("release latch: compartment ");
      break;
    case SelectionEffect::Idle:
      Serial.println("idle light");
      return;
    case SelectionEffect::Stopped:
      Serial.print("stopped: inspect compartment ");
      break;
  }

  Serial.println(step.compartment);
}

void SerialSelectionPort::DisableActuators() {
  Serial.println("safety request: inhibit all actuators (log only)");
}

void SerialSelectionPort::StartVibration(uint8_t compartment) {
  Serial.print("safety request: vibrate compartment ");
  Serial.println(compartment);
}

void SerialSelectionPort::ReleaseLatch(uint8_t compartment) {
  Serial.print("safety request: release compartment ");
  Serial.println(compartment);
}

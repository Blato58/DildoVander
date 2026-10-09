#pragma once

#include <array>
#include <stdint.h>

#include "BoardPins.h"

struct ServoCalibration {
  uint16_t restUs = 0;
  uint16_t raisedUs = 0;
  uint16_t travelMs = 0;
};

struct PhysicalInputs {
  bool enabled = false;
  bool watchdogGood = false;
  bool button = false;
  uint8_t restingMask = 0;
  uint8_t raisedMask = 0;
};

// The SDK adapter coordinates the shared I2C bus and drops direct RUN_REQUEST
// before attempting any I2C recovery. FullOff addresses every PCA output.
class PhysicalIo {
 public:
  virtual ~PhysicalIo() = default;
  virtual void RunRequest(bool enabled) = 0;
  virtual bool FullOff() = 0;
  virtual bool Servo(uint8_t channel, uint16_t pulseUs) = 0;
  virtual bool Vibration(uint8_t channel, bool enabled) = 0;
  virtual bool Lights(const std::array<uint8_t, 108>& grb) = 0;
  virtual bool ReadInputs(PhysicalInputs& inputs) = 0;
  virtual void Heartbeat(uint64_t nowMs) = 0;
};

class PhysicalEffects final {
 public:
  explicit PhysicalEffects(PhysicalIo& io) : io_(io) {}
  bool Configure(const std::array<ServoCalibration, 6>& calibration,
                 uint64_t revision, bool inhibitVerified);
  void Tick(uint64_t nowMs);
  void Disable();
  void Fault();
  bool RecoverAfterInspection();
  bool Highlight(uint8_t compartment, uint8_t red, uint8_t green, uint8_t blue);
  bool StartVibration(uint8_t compartment);
  bool Move(uint8_t compartment, bool raise);
  bool Healthy() const { return configured_ && !fault_; }
  bool Ready(uint8_t compartment) const;
  bool IsRaised(uint8_t compartment) const;
  bool IsResting(uint8_t compartment) const;
  bool ButtonPressed() const { return inputs_.button; }
  uint64_t CalibrationRevision() const { return configured_ ? revision_ : 0; }

 private:
  bool SafeInputs() const;
  bool Position(uint8_t compartment, bool raise) const;
  PhysicalIo& io_;
  std::array<ServoCalibration, 6> calibration_{};
  PhysicalInputs inputs_{};
  uint8_t candidateRest_ = 0, candidateRaised_ = 0;
  uint64_t positionChangedAt_ = 0;
  uint64_t revision_ = 0, nowMs_ = 0, lastPass_ = 0, activeAt_ = 0, heartbeatAt_ = 0;
  uint8_t active_ = 0;
  bool configured_ = false, fault_ = true, observed_ = false, servo_ = false, raising_ = false;
};

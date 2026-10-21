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
  bool button = false;
};

// The SDK adapter coordinates I2C and disables PCA outputs directly via OE
// before any register change. FullOff addresses every PCA output.
class PhysicalIo {
 public:
  virtual ~PhysicalIo() = default;
  virtual void EnableOutputs(bool enabled) = 0;
  virtual bool FullOff() = 0;
  virtual bool Servo(uint8_t channel, uint16_t pulseUs) = 0;
  virtual bool Vibration(uint8_t channel, bool enabled) = 0;
  virtual bool Lights(const std::array<uint8_t, 108>& grb) = 0;
  virtual bool ReadInputs(PhysicalInputs& inputs) = 0;
  // Optional direct shutdown for an SDK adapter. Recovery may clear only the
  // epoch it inspected; a concurrent new Stop must retain the output gate.
  virtual void RequestUrgentStop() { EnableOutputs(false); }
  virtual uint32_t StopEpoch() const { return 0; }
  virtual bool ClearUrgentStopAfterInspection(uint32_t) { return true; }
};

class PhysicalEffects final {
 public:
  static constexpr uint32_t VibrationDurationMs = 150;
  explicit PhysicalEffects(PhysicalIo& io) : io_(io) {}
  bool Configure(const std::array<ServoCalibration, 6>& calibration,
                 uint64_t revision);
  void Tick(uint64_t nowMs);
  void Disable();
  void Fault();
  bool RecoverAfterInspection();
  bool Highlight(uint8_t compartment, uint8_t red, uint8_t green, uint8_t blue);
  // One compartment bit or zero (all lights off); multi-segment masks fail.
  bool Illuminate(uint8_t mask, uint8_t red, uint8_t green, uint8_t blue);
  bool IlluminateAtLevel(uint8_t mask, uint8_t red, uint8_t green, uint8_t blue,
                         uint8_t level);
  bool StartVibration(uint8_t compartment);
  bool Move(uint8_t compartment, bool raise);
  // Passive boot evidence never authorizes motion or confirms tray positions.
  // Actual I/O and control-loop failures remain latched until service recovery.
  bool PassiveHealthy() const { return configured_ && observed_ && !fault_; }
  bool Healthy() const { return PassiveHealthy() && !inspectionRequired_; }
  bool Ready(uint8_t compartment) const;
  uint32_t MotionTimeoutMs(uint8_t compartment) const;
  bool IsRaised(uint8_t compartment) const;
  bool IsResting(uint8_t compartment) const;
  bool ButtonPressed() const { return inputs_.button; }
  uint64_t CalibrationRevision() const { return configured_ ? revision_ : 0; }

 private:
  bool Position(uint8_t compartment, bool raise) const;
  PhysicalIo& io_;
  std::array<ServoCalibration, 6> calibration_{};
  PhysicalInputs inputs_{};
  // Commanded positions after calibrated travel, never measured feedback.
  uint8_t restingMask_ = 0, raisedMask_ = 0;
  uint64_t revision_ = 0, nowMs_ = 0, lastPass_ = 0, activeAt_ = 0;
  uint8_t active_ = 0;
  bool configured_ = false, fault_ = false, inspectionRequired_ = true;
  bool observed_ = false, servo_ = false, raising_ = false;
};

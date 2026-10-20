#pragma once

#include <array>
#include <cstdint>
#include <string>

#include "physical_effects/PhysicalEffects.h"
#include "physical_effects/PresentationAudioPort.h"
#include "dance/DancePlayerPort.h"

// This model receives accepted driver writes only. Its positions never become
// PhysicalInputs or SafetyPort feedback; production calibrated timers own those.
class VirtualHardware final : public PhysicalIo {
 public:
  bool enabled = false;
  // A process restart cannot infer virtual pose from the command journal.
  // Only an explicit simulated operator inspection establishes a known pose.
  bool poseKnown = false;
  std::array<uint8_t, 108> led{};
  std::array<uint16_t, 6> pulse{};
  std::array<double, 6> position{};
  std::array<bool, 6> jammed{};
  uint8_t vibrationMask = 0;
  std::string fault = "none";
  uint8_t faultSlot = 0;
  uint64_t servoWrites = 0, lightWrites = 0, vibrationWrites = 0;
  bool orderingHealthy = true;

  void Advance(uint64_t nowMs);
  void Inspect();
  void EnableOutputs(bool value) override { enabled = value; }
  bool FullOff() override;
  bool Servo(uint8_t channel, uint16_t pulseUs) override;
  bool Vibration(uint8_t channel, bool value) override;
  bool Lights(const std::array<uint8_t, 108>& frame) override;
  bool ReadInputs(PhysicalInputs& inputs) override {
    inputs = PhysicalInputs{};
    return true;
  }

 private:
  uint8_t activeServo_ = 0;
  double target_ = 0;
  uint64_t lastMs_ = 0;
};

class SimulatedTones final : public PresentationAudioPort {
 public:
  void Cue(uint16_t, uint16_t) override { ++queued; }
  CueResult TryCue(uint16_t frequency, uint16_t duration) override {
    Cue(frequency, duration);
    active = true;
    return CueResult::Requested;
  }
  void StopCues() override { active = false; }
  bool active = false;
  uint64_t queued = 0;
};

// Deterministic synthetic media cursor; no samples, speaker or SD device. The
// production coordinator receives honest simulated enqueue/drain evidence.
class SimulatedDancePlayer final : public kufr::dance::DancePlayerPort {
 public:
  kufr::dance::DanceStatus Status() const override {
    kufr::dance::DanceStatus status;
    status.available = true;
    status.phase = phase_;
    status.track = "synthetic-120bpm";
    status.positionMs = position_;
    status.durationMs = 8000;
    status.bpm = 120;
    status.red = 90;
    status.green = 30;
    status.blue = 220;
    status.vibrationEveryBeats = 2;
    return status;
  }
  bool Start(const std::string& id) override {
    if (phase_ == "playing" || id == lastId_) return false;
    lastId_ = id;
    started_ = now_;
    position_ = 0;
    phase_ = "playing";
    return true;
  }
  void Stop() override { if (phase_ == "playing") phase_ = "stopped"; }
  void Advance(uint64_t now) {
    now_ = now;
    if (phase_ != "playing") return;
    const uint64_t elapsed = now - started_;
    position_ = static_cast<uint32_t>(elapsed < 8000 ? elapsed : 8000);
    if (elapsed >= 8000) phase_ = "completed";
  }
 private:
  uint64_t now_ = 0, started_ = 0;
  uint32_t position_ = 0;
  std::string phase_ = "idle", lastId_;
};

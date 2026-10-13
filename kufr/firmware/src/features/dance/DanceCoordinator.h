#pragma once
#include "DancePlayerPort.h"
#include "../physical_effects/PhysicalEffects.h"

namespace kufr { namespace dance {
class DanceCoordinator final : public LocalDancePort {
 public:
  DanceCoordinator(DancePlayerPort& player, PhysicalEffects& effects) : player_(player), effects_(effects) {}
  DanceStatus DanceState() const override;
  bool StartDance(const std::string& requestId) override;
  void StopDance() override;
  void Tick(uint64_t nowMs);
  bool Reserved() const { return ownsEffects_ || IsDanceActive(player_.Status()); }
 private:
  bool SafeResting() const;
  void ClearEffects();
  void FailEffects();
  DancePlayerPort& player_;
  PhysicalEffects& effects_;
  std::string error_;
  uint64_t lastBeat_ = 0;
  bool beatSeen_ = false, lightsActive_ = false;
  bool ownsEffects_ = false;
  uint8_t lastLevel_ = 0;
};
} }

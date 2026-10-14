#pragma once
#include "DancePlayerPort.h"
#include "../physical_effects/PhysicalEffects.h"
#include "../physical_effects/PresentationDiagnostics.h"

namespace kufr { namespace dance {
class DanceCoordinator final : public LocalDancePort {
 public:
  DanceCoordinator(DancePlayerPort& player, PhysicalEffects& effects) : player_(player), effects_(effects) {}
  DanceStatus DanceState() const override;
  bool StartDance(const std::string& requestId) override;
  void StopDance() override;
  void Tick(uint64_t nowMs);
  bool Reserved() const { return ownsEffects_ || IsDanceActive(player_.Status()); }
  const PresentationDiagnostics& Diagnostics() const { return diagnostics_; }
  void BindBootIdentity(const std::string& bootId) { if (!Reserved()) bootId_ = bootId; }
 private:
  bool SafeResting() const;
  void ClearEffects();
  void FailEffects();
  void Trace(const char* kind, const char* reason, const DanceStatus& status);
  DancePlayerPort& player_;
  PhysicalEffects& effects_;
  std::string error_;
  uint64_t lastBeat_ = 0;
  bool beatSeen_ = false, lightsActive_ = false;
  bool ownsEffects_ = false;
  bool stopRequested_ = false;
  uint8_t lastLevel_ = 0;
  uint64_t nowMs_ = 0;
  uint32_t lastPositionMs_ = 0, audioGeneration_ = 0;
  std::string bootId_ = "unavailable";
  PresentationDiagnostics diagnostics_;
};
} }

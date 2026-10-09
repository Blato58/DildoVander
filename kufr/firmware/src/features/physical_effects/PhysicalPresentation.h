#pragma once

#include "PhysicalEffects.h"
#include "../device_control/DeviceControlRuntime.h"
#include "../wand_presentation/WandPresentation.h"

// Runs only on the control owner. Journal admission precedes the first effect;
// endpoint feedback, rather than elapsed servo time, permits completion.
class PhysicalPresentation final : public WandPresentationPort {
 public:
  PhysicalPresentation(PhysicalEffects& effects, DeviceControlRuntime& runtime)
      : effects_(effects), runtime_(runtime) {}
  void Tick(uint64_t nowMs);
  bool IsReady(uint8_t compartment) const override { return effects_.Ready(compartment); }
  bool IsSafeToContinue() const override { return effects_.Healthy(); }
  bool IsRaised(uint8_t compartment) const override;
  bool IsResting(uint8_t compartment) const override;
  bool Raise(uint8_t compartment, const std::string& proposalId,
             const ConversationRuntimeSnapshot& snapshot) override;
  bool Lower(uint8_t compartment) override;
  bool Complete(uint8_t compartment) override;
  void DisableAndFault() override;
 private:
  enum class Phase { Idle, Search, Highlight, Vibration, Gap, Raise, Raised, Lower };
  PhysicalEffects& effects_;
  DeviceControlRuntime& runtime_;
  Phase phase_ = Phase::Idle;
  uint64_t nowMs_ = 0, phaseAt_ = 0;
  uint8_t compartment_ = 0, search_ = 0, pulses_ = 0;
};

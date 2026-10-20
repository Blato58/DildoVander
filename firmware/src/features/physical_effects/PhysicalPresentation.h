#pragma once

#include "PhysicalEffects.h"
#include "PresentationAudioPort.h"
#include "PresentationScore.h"
#include "PresentationDiagnostics.h"
#include "../device_control/DeviceControlRuntime.h"
#include "../wand_presentation/WandPresentation.h"

// Runs only on the control owner. Journal admission precedes the first effect;
// calibrated elapsed travel permits completion; positions are not measured.
class PhysicalPresentation final : public WandPresentationPort {
 public:
  PhysicalPresentation(PhysicalEffects& effects, DeviceControlRuntime& runtime)
      : effects_(effects), runtime_(runtime) {}
  void Tick(uint64_t nowMs);
  // Explicit command selection is idle-only; callers that do not select retain
  // the normal first/main and subsequent/short behavior.
  bool SelectVariant(bool shortVariant);
  // Capture with a newly created operation; retries retain the saved value.
  static uint64_t OperationFingerprint(bool shortVariant = false);
  uint64_t CurrentOperationFingerprint() const override;
  bool ReserveOperation(uint8_t compartment, const std::string& proposalId,
      const ConversationRuntimeSnapshot& snapshot) override;
  void CancelOperationReservation() override { runtime_.CancelAutonomousReservation(); }
  void ObserveOwnerTime(uint64_t nowMs) override { nowMs_ = nowMs; }
  bool AdmissionWasDuplicateOrConflict() const override { return replayRejected_; }
  void BindAudio(PresentationAudioPort& audio) { audio_ = &audio; }
  void BindClock(uint64_t (*nowMs)(void*), void* context) {
    if (phase_ == Phase::Idle) { clockNowMs_ = nowMs; clockContext_ = context; }
  }
  // Set at boot while idle; the accepted execution retains its copy.
  void BindBootIdentity(const std::string& bootId) { if (phase_ == Phase::Idle) bootId_ = bootId; }
  const PresentationDiagnostics& Diagnostics() const { return diagnostics_; }
  bool CancelOperation(const std::string& operationId, uint64_t generation);
  bool StartAccent(const char* sceneId, const std::string& intentId,
                   uint64_t generation, uint64_t expiresAtMs, uint64_t nowMs, uint8_t target = 0, bool preserveConversationSnapshot = false);
  bool StartVibrationPreset(const char* preset, const std::string& intentId,
      uint64_t generation, uint64_t expiresAtMs, uint64_t nowMs, uint8_t target, bool preserveConversationSnapshot = false);
  bool CancelAccent(const std::string& intentId, uint64_t generation);
  bool IsReady(uint8_t compartment) const override { return effects_.Ready(compartment); }
  bool IsSafeToContinue() const override { return effects_.Healthy(); }
  uint64_t RaiseTimeoutMs(uint8_t n) const override {
    const uint32_t preparationMs = repeat_ ? PresentationScore::ShortPreparationMs
                                          : PresentationScore::MainPreparationMs;
    return preparationMs + 1000 + effects_.MotionTimeoutMs(n);
  }
  uint64_t LowerTimeoutMs(uint8_t n) const override {
    return effects_.MotionTimeoutMs(n) + RestDurationMs();
  }
  bool IsRaised(uint8_t compartment) const override;
  bool IsResting(uint8_t compartment) const override;
  bool Raise(uint8_t compartment, const std::string& proposalId,
             const ConversationRuntimeSnapshot& snapshot) override;
  bool Lower(uint8_t compartment) override;
  bool Complete(uint8_t compartment) override;
  void DisableAndFault() override;
 private:
  enum class Phase { Idle, Score, Raise, Raised, Lower, Rest, Accent, Pulse };
  void EndPulse(bool cancelled);
  bool EnterScoreStep(uint64_t dueAt);
  void Trace(const char* kind, uint64_t dueAt, const char* evidence,
             const char* reason = "");
  bool TickAccent();
  void EndAccent(bool cancelled);
  bool RefreshExecutionClock(uint64_t ioStartedAtMs);
  static bool ValidateCompletionClock(void* context);
  const PresentationScore::Step& CurrentStep() const;
  unsigned StepCount() const;
  uint16_t RestDurationMs() const;
  PhysicalEffects& effects_;
  DeviceControlRuntime& runtime_;
  Phase phase_ = Phase::Idle;
  uint64_t nowMs_ = 0, phaseAt_ = 0, startedAt_ = 0, lastObservedAt_ = 0;
  uint8_t compartment_ = 0, scoreIndex_ = 0;
  bool repeat_ = false;
  PresentationAudioPort* audio_ = nullptr;
  std::string bootId_ = "unavailable";
  PresentationDiagnostics diagnostics_;
  const PresentationScore::AccentScene* accent_ = nullptr;
  uint64_t accentExpiresAt_ = 0;
  uint64_t pulseUntil_ = 0;
  uint8_t accentTarget_ = 0;
  bool accentLit_ = false;
  uint8_t accentCueSeen_ = 0, accentCueSkipped_ = 0;
  bool replayRejected_ = false;
  bool vibrationCommanded_ = false;
  uint64_t (*clockNowMs_)(void*) = nullptr;
  void* clockContext_ = nullptr;
  uint64_t completionIoStartedAtMs_ = 0;
  const char* faultReason_ = "recovery-required";
};

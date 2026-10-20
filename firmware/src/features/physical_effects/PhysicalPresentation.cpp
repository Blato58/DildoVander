#include "PhysicalPresentation.h"
#include <cstring>

namespace {
uint64_t ScoreFingerprint(bool repeat) {
  uint64_t hash = 14695981039346656037ull;
  for (const char* byte = PresentationScore::BundleHash; *byte; ++byte) {
    hash ^= static_cast<unsigned char>(*byte); hash *= 1099511628211ull;
  }
  return hash ^ static_cast<uint64_t>(repeat);
}
}

uint64_t PhysicalPresentation::OperationFingerprint(bool shortVariant) {
  return ScoreFingerprint(shortVariant);
}

uint64_t PhysicalPresentation::CurrentOperationFingerprint() const {
  if (phase_ != Phase::Idle || !runtime_.CanCaptureOwnedPresentationFingerprint() ||
      !effects_.Healthy()) return 0;
  return ScoreFingerprint(repeat_);
}

bool PhysicalPresentation::SelectVariant(bool shortVariant) {
  if (phase_ != Phase::Idle || claimedAdmission_ || runtime_.HasAutonomousPresentation() ||
      runtime_.HasDecoration()) return false;
  repeat_ = shortVariant;
  return true;
}

bool PhysicalPresentation::ValidateClaimedOperation(
    const PendingProposal::ClaimedProposal& claim, uint64_t fingerprint,
    uint64_t calibrationRevision, uint64_t nowMs) const {
  return phase_ == Phase::Idle && claim.IsLive(nowMs) &&
      fingerprint == ScoreFingerprint(repeat_) &&
      calibrationRevision == effects_.CalibrationRevision() &&
      effects_.Healthy() && effects_.Ready(claim.Proposal().compartment) &&
      effects_.IsResting(claim.Proposal().compartment) &&
      runtime_.ValidateClaimedAutonomousPresentation(claim, fingerprint, calibrationRevision, nowMs);
}

WandAdmissionProgress PhysicalPresentation::ReserveClaimedOperation(
    const PendingProposal::ClaimedProposal& claim, uint64_t fingerprint,
    uint64_t calibrationRevision, uint64_t nowMs) {
  if (!ValidateClaimedOperation(claim, fingerprint, calibrationRevision, nowMs))
    return WandAdmissionProgress::Rejected;
  claimedAdmission_ = true;
  const auto result = runtime_.ReserveClaimedAutonomousPresentation(
      claim, fingerprint, calibrationRevision, nowMs);
  if (result == DeviceControlRuntime::PresentationProgress::Pending)
    return WandAdmissionProgress::Pending;
  return result == DeviceControlRuntime::PresentationProgress::Succeeded
      ? WandAdmissionProgress::Succeeded : WandAdmissionProgress::Rejected;
}

WandAdmissionProgress PhysicalPresentation::BeginClaimedOperation(
    const PendingProposal::ClaimedProposal& claim, uint64_t fingerprint,
    uint64_t calibrationRevision, uint64_t acceptedAtMs, uint64_t nowMs) {
  replayRejected_ = false;
  if (!ValidateClaimedOperation(claim, fingerprint, calibrationRevision, nowMs))
    return WandAdmissionProgress::Rejected;
  claimedAdmission_ = true;
  nowMs_ = nowMs;
  const auto result = runtime_.BeginClaimedAutonomousPresentation(
      claim, fingerprint, calibrationRevision, nowMs);
  if (result == DeviceControlRuntime::PresentationProgress::Pending)
    return WandAdmissionProgress::Pending;
  if (result == DeviceControlRuntime::PresentationProgress::Rejected)
    return WandAdmissionProgress::Rejected;
  // The durable ACK can arrive after the call's input timestamp. No output may
  // use an expired claim, changed calibration, or a renewed physical start time.
  if (clockNowMs_) nowMs_ = clockNowMs_(clockContext_);
  if (!claim.IsLive(nowMs_) || nowMs_ < nowMs || nowMs_ < acceptedAtMs ||
      calibrationRevision != effects_.CalibrationRevision() ||
      fingerprint != ScoreFingerprint(repeat_) ||
      !effects_.Healthy() || !effects_.Ready(claim.Proposal().compartment) ||
      !effects_.IsResting(claim.Proposal().compartment)) {
    DisableAndFault();
    return WandAdmissionProgress::Rejected;
  }
  claimedAdmission_ = false;
  return StartEffects(claim.Proposal().compartment, claim.Proposal().proposalId,
      claim.Snapshot(), acceptedAtMs, claim.DeadlineMs())
      ? WandAdmissionProgress::Succeeded : WandAdmissionProgress::Rejected;
}

void PhysicalPresentation::CancelClaimedOperation() {
  claimedAdmission_ = false;
  runtime_.CancelClaimedAutonomousPresentation();
}

bool PhysicalPresentation::ReserveOperation(uint8_t compartment, const std::string& proposalId,
                                          const ConversationRuntimeSnapshot& snapshot) {
  return phase_ == Phase::Idle && effects_.Ready(compartment) && effects_.IsResting(compartment) &&
      runtime_.ReserveAutonomousPresentation(proposalId, compartment, snapshot, ScoreFingerprint(repeat_));
}

bool PhysicalPresentation::Raise(uint8_t compartment, const std::string& proposalId,
                                  const ConversationRuntimeSnapshot& snapshot) {
  const uint64_t acceptedAtMs = nowMs_;
  replayRejected_ = runtime_.FindOperation(proposalId, compartment, ScoreFingerprint(repeat_),
                                          snapshot.operationEpoch)
      != CommandJournal::Lookup::Missing && !runtime_.HasAutonomousReservation(
          proposalId, compartment, ScoreFingerprint(repeat_), snapshot.operationEpoch);
  if (replayRejected_) return false;
  if (proposalId.empty() || proposalId.size() > 96 ||
      phase_ != Phase::Idle || !effects_.Ready(compartment) ||
      !effects_.IsResting(compartment) ||
      !runtime_.BeginAutonomousPresentation(proposalId, compartment, snapshot, ScoreFingerprint(repeat_))) return false;
  return StartEffects(compartment, proposalId, snapshot, acceptedAtMs);
}

bool PhysicalPresentation::StartEffects(uint8_t compartment,
    const std::string& proposalId, const ConversationRuntimeSnapshot& snapshot,
    uint64_t acceptedAtMs, uint64_t expiresAtMs) {
  diagnostics_ = PresentationDiagnostics{};
  diagnostics_.operationId = proposalId;
  diagnostics_.bootId = bootId_;
  diagnostics_.sceneId = repeat_ ? PresentationScore::ShortSceneId : PresentationScore::MainSceneId;
  diagnostics_.sceneVersion = repeat_ ? PresentationScore::ShortSceneVersion : PresentationScore::MainSceneVersion;
  diagnostics_.sceneHash = repeat_ ? PresentationScore::ShortSceneHash : PresentationScore::MainSceneHash;
  diagnostics_.variant = repeat_ ? "short" : "main";
  diagnostics_.generation = snapshot.generation;
  diagnostics_.deviceRevision = snapshot.deviceRevision;
  diagnostics_.calibrationId = effects_.CalibrationRevision();
  diagnostics_.slot = compartment;
  diagnostics_.state = "running";
  compartment_ = compartment;
  scoreIndex_ = 0;
  phase_ = Phase::Score;
  phaseAt_ = startedAt_ = lastObservedAt_ = acceptedAtMs;
  faultReason_ = "recovery-required";
  const bool fresh = RefreshExecutionClock(acceptedAtMs);
  Trace("accepted", nowMs_, "commanded", "journal-persisted");
  if (!fresh || nowMs_ >= expiresAtMs) { DisableAndFault(); return false; }
  if (nowMs_ - phaseAt_ >= CurrentStep().durationMs) {
    faultReason_ = "journal-expired-first-cue";
    DisableAndFault(); return false;
  }
  if (!EnterScoreStep(phaseAt_)) { DisableAndFault(); return false; }
  return true;
}

unsigned PhysicalPresentation::StepCount() const {
  return repeat_ ? sizeof(PresentationScore::Short) / sizeof(PresentationScore::Step)
                 : sizeof(PresentationScore::Main) / sizeof(PresentationScore::Step);
}

const PresentationScore::Step& PhysicalPresentation::CurrentStep() const {
  return repeat_ ? PresentationScore::Short[scoreIndex_] : PresentationScore::Main[scoreIndex_];
}

uint16_t PhysicalPresentation::RestDurationMs() const {
  return repeat_ ? PresentationScore::Short[StepCount() - 1].durationMs
                 : PresentationScore::Main[StepCount() - 1].durationMs;
}

void PhysicalPresentation::Trace(const char* kind, uint64_t dueAt,
                                 const char* evidence, const char* reason) {
  if (phase_ == Phase::Accent) {
    PresentationTraceEvent event;
    event.kind = kind; event.phaseId = PresentationScore::AccentPhases[accent_->phaseOffset + scoreIndex_].phaseId;
    event.phaseIndex = scoreIndex_; event.timeMs = nowMs_; event.dueAt = dueAt;
    event.evidence = evidence; event.reason = reason;
    diagnostics_.Append(event);
    return;
  }
  const auto& step = CurrentStep();
  PresentationTraceEvent event;
  event.kind = kind; event.phaseId = repeat_ ? PresentationScore::ShortPhaseIds[scoreIndex_] : PresentationScore::MainPhaseIds[scoreIndex_];
  event.phaseIndex = scoreIndex_; event.timeMs = nowMs_; event.dueAt = dueAt;
  event.latenessMs = nowMs_ > dueAt ? static_cast<uint32_t>(nowMs_ - dueAt) : 0;
  event.evidence = evidence; event.reason = reason; event.slot = compartment_;
  event.red = step.red; event.green = step.green; event.blue = step.blue;
  event.level = KufrBoard::InitialBrightness;
  event.vibration = std::strcmp(kind, "commanded") == 0 ? vibrationCommanded_ : step.vibration;
  event.toneHz = std::strcmp(kind, "commanded") == 0 ? 0 : step.toneHz;
  uint8_t target = compartment_;
  if (step.target > 0) target = static_cast<uint8_t>(step.target);
  else if (step.target == -2) target = compartment_ % 6 + 1;
  else if (step.target == -3) target = (compartment_ + 4) % 6 + 1;
  event.mask = step.target == -1 ? 63 : static_cast<uint8_t>(1u << (target - 1));
  diagnostics_.Append(event);
}

bool PhysicalPresentation::EnterScoreStep(uint64_t dueAt) {
  if (!RefreshExecutionClock(nowMs_)) return false;
  const auto& step = CurrentStep();
  if (step.stage == PresentationScore::Stage::Prepare && nowMs_ - dueAt >= step.durationMs) return false;
  // Admission covers the whole phase: a denied move must not flash its
  // lights or submit any optional cue before fault cleanup.
  if (step.stage == PresentationScore::Stage::Raise ||
      step.stage == PresentationScore::Stage::Lower) {
    // A late phase cannot begin motion whose mandatory normal tail no longer
    // fits. Calibration is immutable for this execution, never a JSON duration.
    const uint64_t tailMs = repeat_ ? PresentationScore::ShortRemainingMs[scoreIndex_]
                                    : PresentationScore::MainRemainingMs[scoreIndex_];
    const uint64_t deadline = repeat_ ? PresentationScore::ShortDeadlineMs : PresentationScore::MainDeadlineMs;
    if (nowMs_ < startedAt_ || nowMs_ - startedAt_ >= deadline ||
        tailMs >= deadline - (nowMs_ - startedAt_)) return false;
  }
  vibrationCommanded_ = false;
  uint8_t target = compartment_;
  if (step.target > 0) target = static_cast<uint8_t>(step.target);
  else if (step.target == -2) target = compartment_ % 6 + 1;
  else if (step.target == -3) target = (compartment_ + 4) % 6 + 1;
  const uint8_t mask = step.target == -1 ? 63 : static_cast<uint8_t>(1u << (target - 1));
  if (!effects_.Illuminate(mask, step.red, step.green, step.blue)) return false;
  if (step.red * KufrBoard::InitialBrightness / 255 ||
      step.green * KufrBoard::InitialBrightness / 255 ||
      step.blue * KufrBoard::InitialBrightness / 255) diagnostics_.none = false;
  if (step.vibration) {
    if (nowMs_ > dueAt) {
      diagnostics_.reduced = true;
      Trace("skipped", dueAt, "unknown", "late-vibration");
    } else if (!effects_.StartVibration(target)) return false;
    else { vibrationCommanded_ = true; diagnostics_.none = false; }
  }
  if (step.stage == PresentationScore::Stage::Raise ||
      step.stage == PresentationScore::Stage::Lower) {
    if (!effects_.Move(compartment_, step.stage == PresentationScore::Stage::Raise)) return false;
    diagnostics_.none = false;
  }
  Trace("commanded", dueAt, "commanded");
  if (step.toneHz) {
    const auto result = audio_ && nowMs_ == dueAt ? audio_->TryCue(step.toneHz,
        step.stage == PresentationScore::Stage::Raise ? 180 : 100)
        : PresentationAudioPort::CueResult::Unavailable;
    if (result != PresentationAudioPort::CueResult::Requested) diagnostics_.reduced = true;
    Trace(result == PresentationAudioPort::CueResult::Unavailable ? "skipped" : "audio-requested",
          dueAt, "unknown", nowMs_ > dueAt ? "late-audio" :
              (result == PresentationAudioPort::CueResult::Unavailable
                  ? "audio_owned_by_other" : "playback-unverified"));
  }
  if (audio_ && step.stage == PresentationScore::Stage::Rest) audio_->StopCues();
  return true;
}

void PhysicalPresentation::Tick(uint64_t nowMs) {
  // A synchronous admission/completion may have sampled a newer time than
  // the caller captured earlier in the same owner pass.
  if (clockNowMs_) nowMs = clockNowMs_(clockContext_);
  nowMs_ = nowMs;
  if (phase_ == Phase::Idle) return;
  if (phase_ == Phase::Pulse) {
    if (!effects_.Healthy() || nowMs < lastObservedAt_ || runtime_.Generation() != diagnostics_.generation || nowMs >= accentExpiresAt_)
      EndPulse(true);
    else if (nowMs >= pulseUntil_) EndPulse(false);
    lastObservedAt_ = nowMs;
    return;
  }
  if (phase_ == Phase::Accent) {
    if (!effects_.Healthy() || nowMs < lastObservedAt_ || nowMs < startedAt_ ||
        runtime_.Generation() != diagnostics_.generation || nowMs >= accentExpiresAt_ || nowMs - startedAt_ >= accent_->maxDurationMs ||
        effects_.CalibrationRevision() != diagnostics_.calibrationId) {
      EndAccent(true); return;
    }
    lastObservedAt_ = nowMs;
    if (!TickAccent()) DisableAndFault();
    return;
  }
  if (phase_ == Phase::Closing) {
    // The first Complete call disabled outputs. Preserve physical health and
    // cancellation checks, but only the close-ACK callback may mint timing
    // evidence; proof ACK latency cannot extend or revalidate physical motion.
    if (!effects_.Healthy() || !effects_.IsResting(compartment_) ||
        nowMs < lastObservedAt_ || runtime_.Generation() != diagnostics_.generation ||
        effects_.CalibrationRevision() != diagnostics_.calibrationId) {
      DisableAndFault();
    } else {
      lastObservedAt_ = nowMs;
    }
    return;
  }
  const uint64_t deadline = repeat_ ? PresentationScore::ShortDeadlineMs : PresentationScore::MainDeadlineMs;
  if (!effects_.Healthy() || nowMs < lastObservedAt_ || nowMs < phaseAt_ ||
      nowMs - startedAt_ >= deadline ||
      effects_.CalibrationRevision() != diagnostics_.calibrationId) {
    DisableAndFault(); return;
  }
  lastObservedAt_ = nowMs;
  switch (phase_) {
    case Phase::Idle:
    case Phase::Accent: return;
    case Phase::Pulse: return;
    case Phase::Closing: return;
    case Phase::Score:
      if (nowMs - phaseAt_ < CurrentStep().durationMs) return;
      effects_.Disable();
      if (!effects_.Healthy()) { DisableAndFault(); return; }
      Trace("phase-ended", phaseAt_ + CurrentStep().durationMs, "timed-complete");
      phaseAt_ += CurrentStep().durationMs;
      ++scoreIndex_;
      if (scoreIndex_ >= StepCount()) { DisableAndFault(); return; }
      if (CurrentStep().stage == PresentationScore::Stage::Prepare &&
          nowMs - phaseAt_ >= CurrentStep().durationMs) {
        if (!effects_.Highlight(0, 0, 0, 0)) { DisableAndFault(); return; }
        diagnostics_.reduced = true;
        Trace("skipped", phaseAt_, "unknown", "expired-decoration");
        return;
      }
      if (!EnterScoreStep(phaseAt_)) { DisableAndFault(); return; }
      if (CurrentStep().stage == PresentationScore::Stage::Raise) phase_ = Phase::Raise;
      return;
    case Phase::Raise:
      if (effects_.IsRaised(compartment_)) {
        Trace("phase-ended", nowMs, "timed-complete", "calibrated-position-unmeasured");
        ++scoreIndex_;
        if (!EnterScoreStep(nowMs)) { DisableAndFault(); return; }
        phase_ = Phase::Raised;
        phaseAt_ = nowMs;
      }
      return;
    case Phase::Raised:
      if (!effects_.IsRaised(compartment_)) DisableAndFault();
      return;
    case Phase::Lower:
      if (effects_.IsResting(compartment_)) {
        effects_.Disable();
        Trace("phase-ended", nowMs, "timed-complete", "calibrated-position-unmeasured");
        ++scoreIndex_;
        if (!effects_.Healthy() || !EnterScoreStep(nowMs)) { DisableAndFault(); return; }
        phase_ = Phase::Rest;
        phaseAt_ = nowMs;
      }
      return;
    case Phase::Rest:
      if (!effects_.IsResting(compartment_)) DisableAndFault();
      return;
  }
}

bool PhysicalPresentation::IsRaised(uint8_t compartment) const {
  return compartment == compartment_ && phase_ == Phase::Raised && effects_.IsRaised(compartment);
}
bool PhysicalPresentation::IsResting(uint8_t compartment) const {
  return (phase_ == Phase::Idle || (phase_ == Phase::Rest && compartment == compartment_ &&
          nowMs_ - phaseAt_ >= CurrentStep().durationMs)) &&
         effects_.IsResting(compartment);
}
bool PhysicalPresentation::Lower(uint8_t compartment) {
  if (!IsRaised(compartment)) return false;
  ++scoreIndex_;
  if (!EnterScoreStep(nowMs_)) { DisableAndFault(); return false; }
  phase_ = Phase::Lower;
  return true;
}
bool PhysicalPresentation::Complete(uint8_t compartment) {
  if (phase_ != Phase::Closing) {
    if (!IsResting(compartment)) return false;
    completionIoStartedAtMs_ = nowMs_;
    if (!RefreshExecutionClock(completionIoStartedAtMs_)) return false;
    effects_.Disable();
    const bool cleared = effects_.Highlight(0, 0, 0, 0);
    if (!RefreshExecutionClock(completionIoStartedAtMs_) || !cleared || !effects_.Healthy()) return false;
    phase_ = Phase::Closing;
  }
  if (compartment != compartment_ || !effects_.Healthy() || !effects_.IsResting(compartment) ||
      runtime_.Generation() != diagnostics_.generation) {
    completionPending_ = false;
    return false;
  }
  const auto progress = runtime_.AdvanceAutonomousCompletion(compartment, ValidateCompletionClock, this);
  completionPending_ = progress == DeviceControlRuntime::PresentationProgress::Pending;
  if (progress != DeviceControlRuntime::PresentationProgress::Succeeded) return false;
  Trace("completed", nowMs_, "timed-complete", "calibrated-position-unmeasured");
  diagnostics_.state = "completed";
  diagnostics_.evidence = "timed-complete";
  phase_ = Phase::Idle;
  repeat_ = true;
  compartment_ = 0;
  return true;
}
bool PhysicalPresentation::CancelOperation(const std::string& operationId, uint64_t generation) {
  if (phase_ == Phase::Idle || operationId != diagnostics_.operationId.data() ||
      generation != diagnostics_.generation) return false;
  DisableAndFault();
  return true;
}

void PhysicalPresentation::DisableAndFault() {
  if (claimedAdmission_) CancelClaimedOperation();
  completionPending_ = false;
  if (phase_ != Phase::Idle) {
    Trace("fault", nowMs_, "unknown", faultReason_);
    diagnostics_.state = "fault";
    diagnostics_.evidence = "unknown";
  }
  const bool wasAccent = phase_ == Phase::Accent || phase_ == Phase::Pulse;
  if (audio_ && !wasAccent) audio_->StopCues();
  effects_.Fault();
  effects_.Highlight(0, 0, 0, 0);
  phase_ = Phase::Idle;
  if (wasAccent) runtime_.ReleaseDecoration(diagnostics_.generation);
  else runtime_.FaultAutonomousPresentation();
}

bool PhysicalPresentation::RefreshExecutionClock(uint64_t ioStartedAtMs) {
  const uint64_t sampledAtMs = clockNowMs_ ? clockNowMs_(clockContext_) : nowMs_;
  const uint64_t previouslyObservedAtMs = nowMs_;
  nowMs_ = sampledAtMs;
  const uint64_t deadline = repeat_ ? PresentationScore::ShortDeadlineMs : PresentationScore::MainDeadlineMs;
  if (sampledAtMs < previouslyObservedAtMs || sampledAtMs < lastObservedAt_ || sampledAtMs < startedAt_) {
    faultReason_ = "journal-clock-reversed";
    return false;
  }
  if (sampledAtMs - startedAt_ >= deadline) {
    faultReason_ = "journal-deadline-exceeded";
    return false;
  }
  if (sampledAtMs - ioStartedAtMs >= 250) {
    faultReason_ = "journal-owner-stall";
    return false;
  }
  lastObservedAt_ = sampledAtMs;
  return true;
}

bool PhysicalPresentation::ValidateCompletionClock(void* context) {
  auto& presentation = *static_cast<PhysicalPresentation*>(context);
  if (!presentation.RefreshExecutionClock(presentation.completionIoStartedAtMs_)) return false;
  presentation.Trace("completion-validated", presentation.nowMs_, "timed-complete",
      "durable-close-clock-checked");
  return true;
}

bool PhysicalPresentation::StartAccent(const char* sceneId, const std::string& intentId,
                                       uint64_t generation, uint64_t expiresAtMs,
                                       uint64_t nowMs, uint8_t target, bool preserveConversationSnapshot) {
  if (phase_ != Phase::Idle || intentId.empty() || intentId.size() > 96 ||
      target > 6 || nowMs >= expiresAtMs || generation != runtime_.Generation() || !effects_.Healthy()) return false;
  const PresentationScore::AccentScene* candidate = nullptr;
  for (const auto& scene : PresentationScore::Accents)
    if (std::string(scene.sceneId) == sceneId) candidate = &scene;
  // This opt-in port implements LED only. DV11 and audio capabilities fail
  // closed rather than silently claiming a referral or sound was performed.
  if (!candidate || (candidate->requiredCapabilities & ~1u) ||
      candidate->phaseCount == 0 || candidate->phaseCount > 8 ||
      std::string(candidate->sceneId) == "choice-accent") return false;
  for (unsigned phase = 0; phase < candidate->phaseCount; ++phase) {
    const auto& definition = PresentationScore::AccentPhases[candidate->phaseOffset + phase];
    if (!definition.durationMs || definition.cueCount > 8) return false;
    for (unsigned cue = 0; cue < definition.cueCount; ++cue) {
      const auto& effect = PresentationScore::AccentCues[definition.cueOffset + cue];
      const bool supported = effect.kind == PresentationScore::CueKind::LedSolid ||
                             effect.kind == PresentationScore::CueKind::LedFade;
      if ((!supported && effect.required) || (supported && (effect.target <= 0 || effect.target > 6))) return false;
    }
  }
  if (!runtime_.ReserveDecoration(generation, preserveConversationSnapshot)) return false;
  diagnostics_ = PresentationDiagnostics{};
  diagnostics_.operationId = intentId; diagnostics_.bootId = bootId_;
  diagnostics_.sceneId = candidate->sceneId; diagnostics_.sceneVersion = candidate->sceneVersion;
  diagnostics_.sceneHash = candidate->sceneHash; diagnostics_.variant = candidate->variant;
  diagnostics_.generation = generation; diagnostics_.calibrationId = effects_.CalibrationRevision();
  diagnostics_.slot = target; accentTarget_ = target;
  diagnostics_.state = "running"; diagnostics_.evidence = "commanded";
  accent_ = candidate; accentExpiresAt_ = expiresAtMs;
  startedAt_ = phaseAt_ = lastObservedAt_ = nowMs_ = nowMs;
  scoreIndex_ = 0; phase_ = Phase::Accent; accentLit_ = false;
  accentCueSeen_ = accentCueSkipped_ = 0;
  Trace("accepted", nowMs, "commanded", "explicit-decoration");
  if (!TickAccent()) { DisableAndFault(); return false; }
  return true;
}

bool PhysicalPresentation::TickAccent() {
  const auto& definition = PresentationScore::AccentPhases[accent_->phaseOffset + scoreIndex_];
  if (nowMs_ - phaseAt_ >= definition.durationMs) {
    if (!effects_.Highlight(0, 0, 0, 0)) return false;
    Trace("phase-ended", phaseAt_ + definition.durationMs, "timed-complete");
    accentLit_ = false;
    accentCueSeen_ = accentCueSkipped_ = 0;
    phaseAt_ += definition.durationMs;
    if (++scoreIndex_ >= accent_->phaseCount) { --scoreIndex_; EndAccent(false); return true; }
    // One phase transition per control pass. No replay burst after a stall.
    return true;
  }
  bool lit = false;
  const uint64_t elapsed = nowMs_ - phaseAt_;
  for (unsigned index = 0; index < definition.cueCount; ++index) {
    const auto& cue = PresentationScore::AccentCues[definition.cueOffset + index];
    const uint8_t bit = static_cast<uint8_t>(1u << index);
    const bool firstObservation = !(accentCueSeen_ & bit);
    const uint32_t end = cue.phaseEnd ? definition.durationMs : cue.offsetMs + cue.durationMs;
    if (elapsed < cue.offsetMs || (accentCueSkipped_ & bit)) continue;
    if (!(accentCueSeen_ & bit)) {
      accentCueSeen_ |= bit;
      const bool unsupported = cue.kind != PresentationScore::CueKind::LedSolid &&
                               cue.kind != PresentationScore::CueKind::LedFade;
      if (unsupported || elapsed >= end ||
          (elapsed > cue.offsetMs && cue.latePolicy == PresentationScore::LatePolicy::SkipIfLate)) {
        accentCueSkipped_ |= bit;
        Trace("skipped", phaseAt_ + cue.offsetMs, "unknown",
              unsupported ? "unsupported-optional-capability" : "expired-decoration");
        diagnostics_.reduced = true;
        continue;
      }
      if (elapsed > cue.offsetMs && cue.latePolicy == PresentationScore::LatePolicy::AbortShow) return false;
    }
    if (elapsed >= end) continue;
    uint8_t red = cue.red, green = cue.green, blue = cue.blue;
    if (cue.kind == PresentationScore::CueKind::LedFade) {
      const uint32_t progress = static_cast<uint32_t>(elapsed - cue.offsetMs);
      const uint32_t duration = end - cue.offsetMs;
      red = static_cast<uint8_t>(cue.fromRed + (static_cast<int64_t>(cue.red) - cue.fromRed) * progress / duration);
      green = static_cast<uint8_t>(cue.fromGreen + (static_cast<int64_t>(cue.green) - cue.fromGreen) * progress / duration);
      blue = static_cast<uint8_t>(cue.fromBlue + (static_cast<int64_t>(cue.blue) - cue.fromBlue) * progress / duration);
    }
    const uint8_t target = accentTarget_ ? accentTarget_ : static_cast<uint8_t>(cue.target);
    const uint8_t mask = static_cast<uint8_t>(1u << (target - 1));
    if (!effects_.IlluminateAtLevel(mask, red, green, blue, cue.level)) return false;
    if (red * cue.level / 255 || green * cue.level / 255 || blue * cue.level / 255) diagnostics_.none = false;
    if (firstObservation || cue.kind == PresentationScore::CueKind::LedFade) {
      PresentationTraceEvent event;
      event.kind = "commanded"; event.phaseId = definition.phaseId;
      event.phaseIndex = scoreIndex_; event.timeMs = nowMs_; event.dueAt = phaseAt_ + cue.offsetMs;
      event.evidence = "commanded"; event.reason = "led-only";
      event.slot = target; event.mask = mask;
      event.red = red; event.green = green; event.blue = blue;
      event.level = cue.level;
      diagnostics_.Append(event);
    }
    lit = true;
  }
  if (!lit && accentLit_ && !effects_.Highlight(0, 0, 0, 0)) return false;
  accentLit_ = lit;
  return true;
}

void PhysicalPresentation::EndAccent(bool cancelled) {
  if (phase_ != Phase::Accent) return;
  if (!effects_.Highlight(0, 0, 0, 0) || !effects_.Healthy()) { DisableAndFault(); return; }
  const bool skipped = !cancelled && diagnostics_.none;
  Trace(cancelled || skipped ? "skipped" : "completed", nowMs_, cancelled || skipped ? "unknown" : "timed-complete",
        cancelled ? "decoration-revoked" : (skipped ? "no-supported-output" : "led-only"));
  diagnostics_.state = cancelled ? "cancelled" : (skipped ? "skipped" : "completed");
  diagnostics_.evidence = cancelled || skipped ? "unknown" : "timed-complete";
  phase_ = Phase::Idle;
  runtime_.ReleaseDecoration(diagnostics_.generation);
  accent_ = nullptr; accentLit_ = false;
}

bool PhysicalPresentation::CancelAccent(const std::string& intentId, uint64_t generation) {
  if (phase_ == Phase::Pulse && intentId == diagnostics_.operationId.data() && generation == diagnostics_.generation) {
    EndPulse(true); return true;
  }
  if (phase_ != Phase::Accent || intentId != diagnostics_.operationId.data() ||
      generation != diagnostics_.generation) return false;
  EndAccent(true);
  return true;
}

bool PhysicalPresentation::StartVibrationPreset(const char* preset, const std::string& intentId,
    uint64_t generation, uint64_t expiresAtMs, uint64_t nowMs, uint8_t target, bool preserveConversationSnapshot) {
  if (std::string(preset) != "presentation-search-pulse" || phase_ != Phase::Idle || target < 1 || target > 6 ||
      intentId.empty() || intentId.size() > 96 || generation != runtime_.Generation() || nowMs >= expiresAtMs ||
      !effects_.Ready(target) || !effects_.IsResting(target)) return false;
  const PresentationScore::Step* pulse = nullptr;
  for (const auto& step : PresentationScore::Main)
    if (step.stage == PresentationScore::Stage::Prepare && step.vibration) { pulse = &step; break; }
  if (!pulse || !pulse->durationMs || pulse->durationMs > PhysicalEffects::VibrationDurationMs ||
      nowMs + pulse->durationMs >= expiresAtMs || !runtime_.ReserveDecoration(generation, preserveConversationSnapshot)) return false;
  if (!effects_.StartVibration(target)) { runtime_.ReleaseDecoration(generation); return false; }
  diagnostics_ = {}; diagnostics_.operationId = intentId; diagnostics_.bootId = bootId_;
  diagnostics_.sceneId = preset; diagnostics_.sceneHash = PresentationScore::MainSceneHash;
  diagnostics_.sceneVersion = PresentationScore::MainSceneVersion; diagnostics_.generation = generation;
  diagnostics_.calibrationId = effects_.CalibrationRevision(); diagnostics_.slot = target;
  diagnostics_.state = "running"; diagnostics_.evidence = "commanded";
  accentExpiresAt_ = expiresAtMs; pulseUntil_ = nowMs + pulse->durationMs;
  nowMs_ = startedAt_ = lastObservedAt_ = nowMs; phase_ = Phase::Pulse;
  return true;
}

void PhysicalPresentation::EndPulse(bool cancelled) {
  effects_.Disable();
  diagnostics_.state = cancelled ? "cancelled" : "completed";
  diagnostics_.evidence = cancelled ? "unknown" : "timed-complete";
  runtime_.ReleaseDecoration(diagnostics_.generation); phase_ = Phase::Idle;
}

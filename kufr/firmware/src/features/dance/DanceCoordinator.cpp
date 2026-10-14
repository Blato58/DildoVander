#include "DanceCoordinator.h"
#include <algorithm>
#include <cstring>

namespace kufr { namespace dance {
bool DanceCoordinator::SafeResting() const {
  if (!effects_.Healthy()) return false;
  for (uint8_t compartment = 1; compartment <= 6; ++compartment)
    if (!effects_.IsResting(compartment) || !effects_.Ready(compartment)) return false;
  return true;
}
DanceStatus DanceCoordinator::DanceState() const {
  DanceStatus status = player_.Status();
  if (ownsEffects_ && !IsDanceActive(status)) status.phase = "stopping";
  status.available = status.available && SafeResting();
  if (!error_.empty()) {
    status.error = error_;
    if (!IsDanceActive(status)) status.phase = "failed";
  }
  return status;
}
bool DanceCoordinator::StartDance(const std::string& requestId) {
  if (Reserved()) return false;
  if (!SafeResting()) { error_ = "dance_effects_unavailable"; return false; }
  if (!player_.Start(requestId)) return false;
  error_.clear(); beatSeen_ = false; lightsActive_ = false; lastLevel_ = 0; ownsEffects_ = true;
  stopRequested_ = false;
  diagnostics_ = PresentationDiagnostics{};
  diagnostics_.operationId = requestId; diagnostics_.bootId = bootId_;
  diagnostics_.sceneId = "dance"; diagnostics_.sceneVersion = 1;
  diagnostics_.sceneHash = ""; diagnostics_.variant = "delegated-owner";
  diagnostics_.state = "running"; diagnostics_.evidence = "unknown";
  diagnostics_.calibrationId = effects_.CalibrationRevision();
  diagnostics_.audioGeneration = ++audioGeneration_; lastPositionMs_ = 0;
  Trace("accepted", "delegated-audio-owner", player_.Status());
  return true;
}
void DanceCoordinator::ClearEffects() {
  effects_.Disable();
  if (!effects_.Highlight(0, 0, 0, 0) || !effects_.Healthy()) error_ = "dance_effects_failed";
  lightsActive_ = false; lastLevel_ = 0;
}
void DanceCoordinator::StopDance() {
  if (!Reserved()) return;
  player_.Stop(); ClearEffects();
  stopRequested_ = true;
  diagnostics_.state = error_.empty() ? "running" : "fault"; diagnostics_.evidence = "unknown";
  Trace("phase-ended", "stop-requested-not-audible-completion", player_.Status());
  // Keep ownership until Tick observes the player's terminal cleanup.

}
void DanceCoordinator::FailEffects() {
  error_ = "dance_effects_failed";
  player_.Stop(); ClearEffects();
  diagnostics_.state = "fault"; diagnostics_.evidence = "unknown";
  Trace("fault", "dance-effects-failed", player_.Status());
}
void DanceCoordinator::Tick(uint64_t nowMs) {
  nowMs_ = nowMs; // Effects.Tick remains owned by the common control task.
  const DanceStatus status = player_.Status();
  if (ownsEffects_) {
    if (!status.track.empty()) diagnostics_.track = status.track;
    diagnostics_.audioPositionMs = status.positionMs;
    if (status.positionMs < lastPositionMs_) {
      diagnostics_.droppedEvents += static_cast<uint32_t>(diagnostics_.count);
      diagnostics_.count = diagnostics_.first = 0;
      diagnostics_.audioGeneration = ++audioGeneration_;
      beatSeen_ = false;
      ClearEffects();
      Trace("phase-ended", "audio-generation-reset", status);
    }
    lastPositionMs_ = status.positionMs;
  }
  if (!IsDanceActive(status)) {
    if (ownsEffects_) {
      ClearEffects(); beatSeen_ = false; ownsEffects_ = false;
      if (status.phase == "failed" || !status.error.empty()) error_ = "dance_audio_failed";
      if (error_.empty() && std::strcmp(diagnostics_.state, "running") == 0) {
        diagnostics_.state = stopRequested_ ? "cancelled" : "completed"; diagnostics_.evidence = "unknown";
      }
      if (!error_.empty()) { diagnostics_.state = "fault"; diagnostics_.evidence = "unknown"; }
      Trace(error_.empty() ? (stopRequested_ ? "phase-ended" : "completed") : "fault",
            "audio-owner-terminal-not-acoustic-measurement", status);
    }
    return;
  }
  if (!ownsEffects_) return;
  if (!effects_.Healthy()) { FailEffects(); return; }
  if (status.phase != "playing" || status.positionMs < status.beatOffsetMs) {
    if (lightsActive_) ClearEffects();
    return;
  }
  if (status.bpm < 40 || status.bpm > 240) { FailEffects(); return; }
  const uint64_t musicalTime = static_cast<uint64_t>(status.positionMs - status.beatOffsetMs) * status.bpm;
  const uint64_t beat = musicalTime / 60000;
  const uint32_t intoBeatMs = static_cast<uint32_t>(musicalTime % 60000 / status.bpm);
  const bool newBeat = !beatSeen_ || beat > lastBeat_;
  if (newBeat) {
    // Admit only the observed beat; missed beats never queue motor pulses.
    effects_.Disable(); lastBeat_ = beat; beatSeen_ = true;
    diagnostics_.beatIndex = beat;
    if (intoBeatMs < 150 && status.vibrationEveryBeats &&
        beat % status.vibrationEveryBeats == 0 &&
        !effects_.StartVibration(static_cast<uint8_t>(beat % 6 + 1))) { FailEffects(); return; }
  }
  const uint32_t pulseMs = std::min<uint32_t>(180, 60000 / status.bpm / 2);
  const uint8_t level = intoBeatMs >= pulseMs ? 0 :
      static_cast<uint8_t>((pulseMs - intoBeatMs) * 255 / pulseMs);
  if (newBeat || level != lastLevel_) {
    const uint8_t compartment = level ? static_cast<uint8_t>(beat % 6 + 1) : 0;
    if (!effects_.Highlight(compartment, status.red * level / 255,
                            status.green * level / 255, status.blue * level / 255)) { FailEffects(); return; }
    lastLevel_ = level; lightsActive_ = level != 0;
    if (level) diagnostics_.none = false;
    if (newBeat) Trace("commanded", "observed-beat-no-catchup", status);
  }
}
void DanceCoordinator::Trace(const char* kind, const char* reason, const DanceStatus& status) {
  PresentationTraceEvent event;
  event.kind = kind; event.reason = reason; event.phaseId = "dance-beat";
  event.clockDomain = "audio-media"; event.timeMs = event.dueAt = status.positionMs;
  event.evidence = std::strcmp(kind, "commanded") == 0 ? "commanded" : "unknown";
  event.slot = status.phase == "playing" && lastLevel_ ? static_cast<uint8_t>(lastBeat_ % 6 + 1) : 0;
  event.mask = event.slot ? static_cast<uint8_t>(1u << (event.slot - 1)) : 0;
  event.red = static_cast<uint8_t>(status.red * lastLevel_ / 255);
  event.green = static_cast<uint8_t>(status.green * lastLevel_ / 255);
  event.blue = static_cast<uint8_t>(status.blue * lastLevel_ / 255);
  event.level = KufrBoard::InitialBrightness;
  diagnostics_.audioPositionMs = status.positionMs;
  diagnostics_.Append(event);
}
} }

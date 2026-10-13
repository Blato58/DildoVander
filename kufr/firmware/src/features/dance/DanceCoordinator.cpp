#include "DanceCoordinator.h"
#include <algorithm>

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
  if (!IsDanceActive(player_.Status())) ownsEffects_ = false;
}
void DanceCoordinator::FailEffects() {
  error_ = "dance_effects_failed";
  player_.Stop(); ClearEffects();
}
void DanceCoordinator::Tick(uint64_t nowMs) {
  (void)nowMs; // Effects.Tick remains owned by the common control task.
  const DanceStatus status = player_.Status();
  if (!IsDanceActive(status)) {
    if (ownsEffects_) { ClearEffects(); beatSeen_ = false; ownsEffects_ = false; }
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
  }
}
} }

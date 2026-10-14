#pragma once

#include <atomic>
#include <mutex>
#include <sstream>
#include "../physical_effects/PhysicalEffects.h"
#include "../physical_effects/PresentationDiagnostics.h"
#include "../physical_effects/PresentationScore.h"
#include "../conversation/ConversationRuntimeState.h"
#include "../physical_effects/PresentationAudioPort.h"

class TwinObservedAudio final : public PresentationAudioPort {
 public:
  explicit TwinObservedAudio(PresentationAudioPort& audio) : audio_(audio) {}
  void Cue(uint16_t hz, uint16_t ms) override { TryCue(hz, ms); }
  CueResult TryCue(uint16_t hz, uint16_t ms) override {
    const auto result = audio_.TryCue(hz, ms);
    status_ = result == CueResult::Requested ? "tone-enqueue-accepted" :
        result == CueResult::Unverified ? "tone-enqueue-unverified" : "tone-unavailable";
    return result;
  }
  void StopCues() override { audio_.StopCues(); status_ = "tone-cancel-requested"; }
  const char* Status() const { return status_; }
 private:
  PresentationAudioPort& audio_;
  const char* status_ = "unknown";
};

// Records only driver-accepted outputs. No positional input is invented.
class TwinObservedIo final : public PhysicalIo {
 public:
  explicit TwinObservedIo(PhysicalIo& io) : io_(io) {}
  void EnableOutputs(bool enabled) override { io_.EnableOutputs(enabled); enabled_ = enabled; }
  bool FullOff() override {
    if (!io_.FullOff()) return false;
    vibrationMask_ = 0;
    return true;
  }
  bool Servo(uint8_t channel, uint16_t pulseUs) override {
    if (!io_.Servo(channel, pulseUs)) return false;
    if (channel < 6) pulse_[channel] = pulseUs;
    return true;
  }
  bool Vibration(uint8_t channel, bool enabled) override {
    if (!io_.Vibration(channel, enabled)) return false;
    if (channel >= 6 && channel < 12) {
      const auto bit = static_cast<uint8_t>(1u << (channel - 6));
      if (enabled) vibrationMask_ |= bit;
      else vibrationMask_ &= static_cast<uint8_t>(~bit);
    }
    return true;
  }
  bool Lights(const std::array<uint8_t, 108>& frame) override {
    if (!io_.Lights(frame)) return false;
    led_ = frame;
    ledKnown_ = true;
    return true;
  }
  bool ReadInputs(PhysicalInputs& inputs) override { return io_.ReadInputs(inputs); }
  std::array<uint16_t, 6> pulse_{};
  std::array<uint8_t, 108> led_{};
  bool enabled_ = false;
  bool ledKnown_ = false;
  uint8_t vibrationMask_ = 0;
 private:
  PhysicalIo& io_;
};

struct TwinTelemetrySnapshot {
  PresentationDiagnostics trace;
  std::array<uint16_t, 6> pulse{};
  std::array<uint8_t, 108> led{};
  // 0 unknown, 1 rest, 2 raised, 3 commanded motion. Never measurements.
  std::array<uint8_t, 6> estimate{};
  uint64_t sequence = 0, timeMs = 0, revision = 0, generation = 0, calibration = 0;
  uint8_t readyMask = 0, vibrationMask = 0;
  const char* state = "idle";
  const char* audio = "unavailable";
  uint8_t slot = 0;
  bool enabled = false, recovery = true, ledKnown = false;
};

// Producer only copies bounded POD/ring state with try_lock. Serialization and
// HTTPS run elsewhere; lost publication never changes device decisions.
class TwinTelemetryMailbox final {
 public:
  bool Publish(const TwinTelemetrySnapshot& snapshot) {
    std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
    if (!lock.owns_lock()) { skipped_.fetch_add(1); return false; }
    if (pending_) coalesced_.fetch_add(1);
    latest_ = snapshot;
    pending_ = true;
    return true;
  }
  bool Take(TwinTelemetrySnapshot& snapshot) {
    std::lock_guard<std::mutex> lock(mutex_);
    if (!pending_) return false;
    snapshot = latest_;
    pending_ = false;
    return true;
  }
  uint64_t Dropped() const { return skipped_.load() + coalesced_.load() + failures_.load(); }
  void Failed() { failures_.fetch_add(1); }
 private:
  std::mutex mutex_;
  TwinTelemetrySnapshot latest_;
  bool pending_ = false;
  std::atomic<uint64_t> skipped_{0}, coalesced_{0}, failures_{0};
};

inline std::string ExportTwinTelemetry(const TwinTelemetrySnapshot& s,
    const std::string& deviceId, const std::string& bootId, const char* version,
    const char* build, uint64_t dropped) {
  // Identities are validated provisioning/generated ASCII identifiers. This
  // exporter has no access to text, media buffers or credentials.
  std::ostringstream out;
  out << "{\"schemaVersion\":1,\"source\":{\"mode\":\"live\",\"instanceId\":\""
      << deviceId << "\",\"deviceId\":\"" << deviceId << "\",\"bootId\":\"" << bootId
      << "\",\"sessionId\":\"" << bootId << "\",\"firmwareVersion\":\"" << version
      << "\",\"buildId\":\"" << build << "\",\"hardwareProfile\":\"B-effects\",\"sceneBundleHash\":\""
      << PresentationScore::BundleHash << "\",\"cadManifestRevision\":null,\"calibrationRevision\":";
  if (s.calibration) out << '\"' << s.calibration << '\"'; else out << "null";
  out << "},\"sequence\":\"" << s.sequence << "\",\"timeMs\":\"" << s.timeMs
      << "\",\"revision\":\"" << s.revision << "\",\"reported\":{\"state\":\"" << s.state
      << "\",\"slot\":" << unsigned(s.slot) << ",\"readyMask\":" << unsigned(s.readyMask)
      << ",\"generation\":\"" << s.generation << "\",\"needsRecovery\":" << (s.recovery ? "true" : "false")
      << ",\"physicalVerification\":\"unavailable\",\"outputsEnabled\":" << (s.enabled ? "true" : "false")
      << ",\"servo\":[";
  for (unsigned i = 0; i < 6; ++i) {
    if (i) out << ',';
    out << "{\"slot\":" << i + 1 << ",\"pulseUs\":";
    if (s.pulse[i]) out << s.pulse[i]; else out << "null";
    out << ",\"estimate\":\"" << (s.estimate[i] == 1 ? "rest" : s.estimate[i] == 2 ? "raised" : s.estimate[i] == 3 ? "moving" : "unknown") << "\"}";
  }
  out << "],\"ledGrb\":";
  if (s.ledKnown) {
    out << '[';
    for (unsigned i = 0; i < s.led.size(); ++i) { if (i) out << ','; out << unsigned(s.led[i]); }
    out << ']';
  } else out << "null";
  out << ",\"vibrationMask\":" << unsigned(s.enabled ? s.vibrationMask : 0)
      << ",\"audio\":\"" << s.audio << "\"},\"model\":null,\"trace\":" << ExportPresentationTrace(s.trace)
      << ",\"transport\":{\"droppedSnapshots\":\"" << dropped
      << "\"},\"capabilities\":[\"observe\",\"presentation\",\"driver-outputs\"]}";
  return out.str();
}

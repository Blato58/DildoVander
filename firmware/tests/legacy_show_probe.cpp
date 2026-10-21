// Shared host-only probe, compiled unchanged against historical and current core.
#include "physical_effects_test_support.h"

#include <iostream>
#include <sstream>
#include <vector>

struct OutputLog {
  uint64_t nowMs = 0, startedAt = 0;
  bool enabled = false;
  std::vector<std::string> events;

  void Record(const char* type, const std::string& fields = "") {
    if (!enabled) return;
    std::ostringstream event;
    event << "{\"type\":\"" << type << "\",\"atMs\":" << nowMs - startedAt << fields << '}';
    events.push_back(event.str());
  }
};

class RecordingIo final : public PhysicalIo {
 public:
  explicit RecordingIo(OutputLog& log) : log_(log) {}
  FakeIo actual;

  void EnableOutputs(bool enabled) override {
    if (!enabled && actual.run) {
      log_.Record("actuator-off", ",\"channel\":" + std::to_string(actual.channel) +
                  ",\"durationMs\":" + std::to_string(log_.nowMs - activeAt_));
    }
    if (enabled && !actual.run) activeAt_ = log_.nowMs;
    actual.EnableOutputs(enabled);
  }
  bool FullOff() override { return actual.FullOff(); }
  bool Servo(uint8_t channel, uint16_t pulseUs) override {
    log_.Record("servo", ",\"channel\":" + std::to_string(channel) +
                ",\"pulseUs\":" + std::to_string(pulseUs));
    return actual.Servo(channel, pulseUs);
  }
  bool Vibration(uint8_t channel, bool enabled) override {
    log_.Record("vibration", ",\"channel\":" + std::to_string(channel) +
                ",\"enabled\":" + (enabled ? "true" : "false"));
    return actual.Vibration(channel, enabled);
  }
  bool Lights(const std::array<uint8_t, 108>& frame) override {
    if (frame != actual.pixels) {
      std::ostringstream fields;
      fields << ",\"grb\":[";
      for (size_t index = 0; index < frame.size(); ++index) {
        if (index) fields << ',';
        fields << unsigned(frame[index]);
      }
      fields << ']';
      log_.Record("led", fields.str());
    }
    return actual.Lights(frame);
  }
  bool ReadInputs(PhysicalInputs& value) override { return actual.ReadInputs(value); }
 private:
  OutputLog& log_;
  uint64_t activeAt_ = 0;
};

class RecordingJournal final : public CommandJournal {
 public:
  explicit RecordingJournal(OutputLog& log) : log_(log) {}
  Journal actual;
  bool HasPending() const override { return actual.HasPending(); }
  uint8_t PendingCompartment() const override { return actual.PendingCompartment(); }
  uint8_t ReadyMask() const override { return actual.ReadyMask(); }
  bool Begin(const std::string& id, uint8_t slot) override {
    const bool result = actual.Begin(id, slot);
    log_.Record("journal-begin", ",\"slot\":" + std::to_string(slot) +
                ",\"accepted\":" + (result ? "true" : "false"));
    return result;
  }
  bool Resolve(uint8_t mask) override {
    const bool result = actual.Resolve(mask);
    log_.Record("journal-resolve", ",\"mask\":" + std::to_string(mask));
    return result;
  }
  bool ResolveRestored(uint8_t slot) override {
    const bool result = actual.ResolveRestored(slot);
    log_.Record("journal-restored", ",\"slot\":" + std::to_string(slot) +
                ",\"accepted\":" + (result ? "true" : "false"));
    return result;
  }
  bool PersistReadyMask(uint8_t mask) override { return actual.PersistReadyMask(mask); }
  // The historical base has no proof hook. Forward it when the current core
  // requests proof acknowledgement, without adding a physical output event.
  bool ConfirmTimedPresentation(const std::string& id, uint8_t slot, uint64_t fingerprint) {
    return actual.ConfirmTimedPresentation(id, slot, fingerprint);
  }
 private:
  OutputLog& log_;
};

class RecordingAudio final : public PresentationAudioPort {
 public:
  explicit RecordingAudio(OutputLog& log) : log_(log) {}
  void Cue(uint16_t frequencyHz, uint16_t durationMs) override {
    log_.Record("audio-request", ",\"frequencyHz\":" + std::to_string(frequencyHz) +
                ",\"durationMs\":" + std::to_string(durationMs));
  }
  void StopCues() override { log_.Record("audio-stop"); }
 private:
  OutputLog& log_;
};

struct ProbeFixture {
  OutputLog log;
  RecordingIo io{log};
  PhysicalEffects effects{io};
  SafetyAdapter port{effects};
  SafetyController safety{port};
  SelectionSequence sequence{safety, true};
  RecordingJournal journal{log};
  DeviceControl control{"device", "boot", sequence, journal, 1};
  ConversationRuntimeState state{false, 0, ""};
  DeviceControlRuntime runtime{control, sequence, state, "catalog"};
  PhysicalPresentation adapter{effects, runtime};
  WandPresentation presentation{adapter};
  RecordingAudio audio{log};

  ProbeFixture() {
    std::array<ServoCalibration, 6> calibration;
    calibration.fill({1100, 1900, 500});
    assert(effects.Configure(calibration, 1));
    effects.Tick(0); effects.Tick(30);
    assert(effects.RecoverAfterInspection());
    assert(runtime.BindPresentation(presentation));
    assert(runtime.RecoverAfterInspection());
    adapter.BindAudio(audio);
    Tick(30); // Align the historical cached PhysicalPresentation clock.
  }
  void Tick(uint64_t now) {
    log.nowMs = now;
    presentation.CheckDeadline(now);
    effects.Tick(now);
    SelectionStep step;
    runtime.Tick(now, step);
    presentation.CheckDeadline(now);
    adapter.Tick(now);
  }
  void Run(uint8_t slot, const char* variant, uint64_t startMs) {
    Tick(startMs);
    log.events.clear(); log.startedAt = startMs; log.enabled = true;
    ConversationRuntimeSnapshot snapshot;
    assert(state.Read(snapshot));
    TurnProposal proposal;
    proposal.proposalId = std::string("legacy-") + std::to_string(slot) + '-' + variant;
    proposal.compartment = slot;
    proposal.catalogVersion = snapshot.catalogVersion;
    proposal.generation = snapshot.generation;
    proposal.deviceRevision = snapshot.deviceRevision;
    PendingProposal pending;
    assert(pending.Store(proposal, snapshot, state, startMs, 30000));
    assert(presentation.Start(pending, state, startMs));
    uint64_t endMs = startMs;
    while (presentation.State() != WandPresentationState::Idle && endMs - startMs <= 15000) {
      endMs += 10;
      Tick(endMs);
      assert(presentation.State() != WandPresentationState::Fault);
    }
    assert(endMs - startMs < 15000);
    assert(effects.IsResting(slot) && !io.actual.run && !journal.actual.pending);
    assert(journal.actual.mask == 63);
    for (uint8_t value : io.actual.pixels) assert(value == 0);
    log.Record("completed", ",\"resting\":true,\"outputsEnabled\":false,\"pending\":false,\"readyMask\":63");
    log.enabled = false;
    std::cout << "{\"slot\":" << unsigned(slot) << ",\"variant\":\"" << variant
              << "\",\"durationMs\":" << endMs - startMs
              << ",\"hardDeadlineMs\":" << WandPresentation::MaximumDurationMs << ",\"events\":[";
    for (size_t index = 0; index < log.events.size(); ++index) {
      if (index) std::cout << ',';
      std::cout << log.events[index];
    }
    std::cout << "]}\n";
  }
};

int main() {
  for (uint8_t slot = 1; slot <= 6; ++slot) {
    ProbeFixture fixture;
    fixture.Run(slot, "main", 30);
    fixture.Run(slot, "short", fixture.log.nowMs + 10);
  }
}

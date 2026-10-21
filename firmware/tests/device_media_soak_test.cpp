#include "device_media_fixture.h"
#include "../src/features/voice_turn/BoundedVoicePlayback.h"
#include "../src/features/safety/SafetyController.h"
#include "../src/features/wand_selection/SelectionSequence.h"

namespace {
struct Raw final : BoundedPlaybackOwner::Port {
  uint64_t played = 0, drained = 0, stops = 0;
  bool Begin(const BoundedPlaybackOwner::Fence& fence) override { return fence.Current(); }
  bool Play(const BoundedPlaybackOwner::Fence& fence, const uint8_t*, size_t length) override {
    if (!fence.Current()) return false;
    played += length; return true;
  }
  bool Drain(const BoundedPlaybackOwner::Fence& fence) override {
    if (!fence.Current()) return false;
    ++drained; return true;
  }
  bool CancelPlayback(const BoundedPlaybackOwner::Fence& fence) override { return fence.Current(); }
};
struct Actuators final : SafetyPort {
  enum class Output { Off, Vibration, Servo };
  Output output = Output::Off;
  unsigned releases = 0;
  void DisableActuators() override { output = Output::Off; }
  void StartVibration(uint8_t slot) override {
    assert(slot >= 1 && slot <= 6 && output == Output::Off); output = Output::Vibration;
  }
  void ReleaseLatch(uint8_t slot) override {
    assert(slot >= 1 && slot <= 6 && output == Output::Off); output = Output::Servo; ++releases;
  }
};
}
int main() {
  constexpr uint64_t Duration = 4ULL * 60 * 60 * 1000;
  constexpr unsigned Sessions = 1440, SessionMs = 10000, TickMs = 20;
  static_assert(static_cast<uint64_t>(Sessions) * SessionMs == Duration, "Exact four-hour workload");
  std::array<uint8_t, BoundedPlaybackOwner::WorkspaceBytes> pcmWorkspace{};
  Raw raw;
  BoundedPlaybackOwner owner(raw, pcmWorkspace.data(), pcmWorkspace.size(),
      [](void* context) { ++static_cast<Raw*>(context)->stops; }, [](void*) { return true; }, &raw);
  uint32_t seed = 0x42a79d01;
  size_t maximumQueue = 0, maximumWire = 0, maximumCapture = 0;
  uint64_t totalInput = 0, completed = 0, interrupted = 0, controlPasses = 0;
  for (unsigned cycle = 0; cycle != Sessions; ++cycle) {
    BoundedVoicePlayback playback(owner, cycle + 1);
    assert(playback.Begin() == VoicePlaybackPort::Progress::Pending && owner.RunOne());
    assert(playback.Begin() == VoicePlaybackPort::Progress::Succeeded);
    const bool live = cycle % 2 == 0, forceStop = cycle % 80 == 79;
    Fixture value(live, false, &playback, 100 + static_cast<uint64_t>(cycle) * SessionMs);
    value.socket.retainSent = false; value.socket.automaticAck = true; value.socket.sent.clear();
    value.socket.now = &value.now; value.modelCapture = true;
    StartWire(value);
    Json(value, "{\"type\":\"media.output\",\"stream\":1,\"responseGeneration\":0,\"format\":\"pcm16-mono-24000\"}");
    if (live) {
      Json(value, "{\"type\":\"media.input\",\"stream\":2,\"inputId\":\"" + Id +
          "\",\"format\":\"pcm16-mono-24000\",\"maxFrameBytes\":4096,\"credit\":1}");
      Json(value, "{\"type\":\"listening\",\"inputId\":\"" + Id + "\",\"responseGeneration\":0}");
    }
    Actuators actuators; SafetyController safety(actuators); safety.InitializeSafe();
    SelectionSequence sequence(safety); assert(sequence.RecoverAfterInspection());
    SelectionStep step; assert(sequence.Start(static_cast<uint8_t>(cycle % 6 + 1), static_cast<uint32_t>(value.now), step));
    const uint64_t start = value.now;
    uint32_t outputSequence = 0;
    bool stopped = false, closeQueued = false;
    for (unsigned offset = 0; offset < SessionMs; offset += TickMs) {
      value.now = start + offset;
      ++controlPasses;
      sequence.ObserveControlLoopPass(static_cast<uint32_t>(value.now), step);
      sequence.Update(static_cast<uint32_t>(value.now), step);
      if (forceStop && offset == 4000) {
        sequence.Stop(step); playback.Stop(); value.session->Stop(); stopped = true;
      }
      if (!stopped && offset < 9000) {
        // Seeded 50..300ms network jitter, with buffered delivery bounded below.
        if (offset && offset % 1000 == 0) {
          seed = seed * 1664525U + 1013904223U;
          value.socket.holdUntil = value.now + 50 + seed % 251;
        }
        std::array<uint8_t, 976> frame{}; std::array<uint8_t, 960> samples{};
        samples.fill(static_cast<uint8_t>(cycle)); size_t used;
        assert(kufr_media::WriteInputPcm(1, outputSequence++, samples.data(), samples.size(), frame.data(), frame.size(), used));
        frame[3] = 2; value.socket.incoming.push_back({true, {frame.begin(), frame.end()}});
        if (live) { ++value.captureFrames; ++totalInput; }
      }
      if (!stopped && offset == 9000) {
        if (live) value.socket.Json("{\"type\":\"speech_stopped\",\"inputId\":\"" + Id + "\",\"responseGeneration\":0}");
        CompletedWire(value, 0);
      }
      if (!stopped) {
        // Buffered socket reads can run between hardware/control ticks. A raw
        // DMA borrow remains on its separate owner, never inside Step.
        for (unsigned burst = 0; burst != 8; ++burst) assert(value.session->Step());
        owner.RunOne();
        if (value.completed && !closeQueued) {
          value.socket.Json("{\"type\":\"closed\",\"reason\":\"conversation_terminal\",\"responseGeneration\":0}");
          closeQueued = true;
        }
      }
      maximumQueue = std::max(maximumQueue, owner.Inspect().queuedBytes);
      maximumWire = std::max(maximumWire, value.socket.incoming.size());
      maximumCapture = std::max(maximumCapture, value.captureFrames);
      assert(owner.Inspect().queuedBytes <= BoundedPlaybackOwner::WorkspaceBytes);
      assert(value.socket.incoming.size() <= 32 && value.captureFrames <= 20);
    }
    assert(!sequence.IsBusy() && actuators.output == Actuators::Output::Off);
    if (stopped) { assert(value.completed == 0); ++interrupted; }
    else { assert(value.completed == 1 && value.engine->Closed()); ++completed; }
    playback.Stop(); assert(!owner.Inspect().workerActive);
  }
  assert(completed + interrupted == Sessions && controlPasses == Duration / TickMs && totalInput > 0);
  std::cout << "PASS simulated no-provider media/show soak: ms=" << Duration << " seed=0x42a79d01 sessions=" << Sessions
      << " completed=" << completed << " interrupted=" << interrupted << " controlPasses=" << controlPasses
      << " PCMplayed=" << raw.played << " queueHigh=" << maximumQueue << " wireHigh=" << maximumWire
      << " captureHigh=" << maximumCapture << " inputFrames=" << totalInput << '\n';
}

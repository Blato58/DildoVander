#if !defined(ESP_PLATFORM)
#error This regression must compile the ESP production branch.
#endif
#include "../src/features/voice_turn/RealtimeSessionEngine.h"
#include <cassert>
#include <iostream>
#include <type_traits>

using Current = std::function<bool()>;
using Clock = std::function<uint64_t()>;
using Phase = std::function<void(const char*)>;
using Completed = std::function<bool(const TurnOperationObservation&)>;
static_assert(!std::is_constructible<RealtimeSessionEngine, VoiceAudioPort&, bool,
    Current, Clock, Clock, Phase, RealtimeSessionEngine::PostPort,
    const TurnCorrelation&, const ConversationRuntimeSnapshot&, Completed>::value,
    "ESP production must require explicit asynchronous media ports");

namespace {
struct RawAudio final : VoiceAudioPort {
  mutable unsigned calls = 0;
  bool Available() const override { ++calls; return true; }
  void Listen(bool) override { ++calls; }
  bool TakeInput(std::vector<uint8_t>&) override { ++calls; return true; }
  bool Play(const std::vector<uint8_t>&) override { ++calls; return true; }
  bool Drain() override { ++calls; return true; }
  bool CancelPlayback() override { ++calls; return true; }
  bool Failed() const override { ++calls; return false; }
  void Stop() override { ++calls; }
};
struct Playback final : VoicePlaybackPort {
  uint64_t next = 0;
  unsigned queued = 0, cancelled = 0, stopped = 0;
  bool Queue(uint64_t, const uint8_t*, size_t length) override { queued += static_cast<unsigned>(length); return true; }
  Progress Drain(uint64_t) override { assert(false); return Progress::Failed; }
  bool RequestDrain(uint64_t, DrainHandle& handle) override { handle = ++next; return true; }
  Progress PollDrain(DrainHandle) override { return Progress::Succeeded; }
  Progress Cancel(uint64_t) override { ++cancelled; return Progress::Succeeded; }
  bool Failed() const override { return false; }
  void Stop() override { ++stopped; }
};
struct Control final : VoiceControlPort {
  uint64_t next = 0;
  unsigned submitted = 0, stopped = 0;
  bool Submit(const Command&, Handle& handle) override { ++submitted; handle = ++next; return true; }
  Progress Poll(Handle) override { return Progress::Succeeded; }
  void Abandon(Handle) override {}
  void Stop() override { ++stopped; }
};
TurnCorrelation Correlation() {
  TurnCorrelation value;
  value.bootId = "boot"; value.conversationId = "conversation"; value.clientTurnId = "turn";
  value.generation = 1; value.deviceRevision = 2; value.operationEpoch = UINT64_MAX;
  return value;
}
ConversationRuntimeSnapshot Snapshot() {
  ConversationRuntimeSnapshot value;
  value.generation = 1; value.deviceRevision = 2; value.operationEpoch = UINT64_MAX;
  value.catalogVersion = "catalog";
  return value;
}
RealtimeEvent Event(const char* type) {
  RealtimeEvent value;
  value.type = type; value.hasResponseGeneration = true; value.responseGeneration = 0;
  return value;
}
TurnOperationObservation Operation() {
  TurnOperationObservation value;
  value.operationId = "operation"; value.kind = "turn"; value.status = TurnOperationStatus::Completed;
  value.correlation = Correlation(); value.reply = "fixture"; value.decision = "no_selection";
  return value;
}
void SupportedAsyncNeverUsesRawAudioOrHttp(bool live) {
  RawAudio raw;
  Playback playback;
  Control control;
  bool capture = false;
  unsigned posts = 0, completed = 0;
  RealtimeSessionEngine engine(raw, live, [] { return true; }, [] { return 1000ULL; },
      [] { return 1700000000000ULL; }, [](const char*) {},
      [&](const std::string&, const void*, size_t, const char*, const std::string&, unsigned) { ++posts; return 204; },
      Correlation(), Snapshot(), [&](const TurnOperationObservation&) { ++completed; return true; },
      {playback, control, [&](bool value) { capture = value; }});
  auto session = Event("session"); session.streamId = "stream"; assert(engine.Event(session));
  auto started = Event("started"); started.operationId = "operation"; started.operation = Operation();
  assert(engine.Event(started));
  const uint8_t pcm[] = {0, 0};
  assert(engine.Audio(0, pcm, sizeof(pcm)));
  if (live) {
    auto checkpoint = Event("playback_checkpoint"); checkpoint.playbackId = "checkpoint"; checkpoint.pcmBytes = 2;
    assert(engine.Event(checkpoint)); assert(engine.Poll()); assert(engine.Poll());
  }
  auto finished = Event("completed"); finished.operationId = "operation"; finished.operation = Operation();
  assert(engine.Event(finished)); assert(engine.Poll()); assert(engine.Poll());
  assert(completed == 1 && playback.queued == 2 && control.submitted == (live ? 2u : 1u));
  assert(engine.Event(Event("closed")) && engine.Closed());
  engine.Stop();
  assert(!capture && playback.stopped == 1 && control.stopped == 1);
  assert(raw.calls == 0 && posts == 0);
}
void LegacyInputApiFailsClosedInsteadOfFallingBack() {
  RawAudio raw;
  Playback playback;
  Control control;
  bool capture = false;
  unsigned posts = 0;
  RealtimeSessionEngine engine(raw, true, [] { return true; }, [] { return 1000ULL; },
      [] { return 1700000000000ULL; }, [](const char*) {},
      [&](const std::string&, const void*, size_t, const char*, const std::string&, unsigned) { ++posts; return 204; },
      Correlation(), Snapshot(), [](const TurnOperationObservation&) { return true; },
      {playback, control, [&](bool value) { capture = value; }});
  auto session = Event("session"); session.streamId = "stream"; assert(engine.Event(session));
  auto listening = Event("listening"); listening.inputId = "input"; assert(engine.Event(listening));
  assert(capture);
  assert(!engine.SendInput(std::vector<uint8_t>({0, 0}), engine.InputEpoch()));
  assert(engine.Failed() && !engine.Current() && !capture);
  assert(playback.stopped == 1 && control.stopped == 1 && raw.calls == 0 && posts == 0);
}
}
int main() {
  SupportedAsyncNeverUsesRawAudioOrHttp(true);
  SupportedAsyncNeverUsesRawAudioOrHttp(false);
  LegacyInputApiFailsClosedInsteadOfFallingBack();
  std::cout << "PASS ESP requires async media ports; no legacy HTTP or synchronous raw-audio fallback\n";
}

#include <cassert>
#include <iostream>
#include <future>
#include <chrono>
#include "../src/features/voice_turn/RealtimeSessionEngine.h"

struct Audio final : VoiceAudioPort {
  bool Available() const override { return true; }
  void Listen(bool enabled) override { listening = enabled; }
  bool TakeInput(std::vector<uint8_t>&) override { return true; }
  bool Play(const std::vector<uint8_t>&) override { ++played; return true; }
  bool Drain() override { ++drained; return true; }
  bool CancelPlayback() override { ++cancelled; return true; }
  void Stop() override {}
  bool Failed() const override { return false; }
  bool listening = false;
  unsigned played = 0, drained = 0, cancelled = 0;
};

void TestResponseReset(bool gptLive, uint64_t operationEpoch = 0) {
  Audio audio;
  TurnCorrelation correlation;
  correlation.deviceId = "fixture";
  correlation.bootId = "boot";
  correlation.conversationId = "conversation";
  correlation.clientTurnId = "input";
  correlation.generation = 4;
  correlation.deviceRevision = 37;
  correlation.operationEpoch = operationEpoch;
  ConversationRuntimeSnapshot snapshot;
  snapshot.generation = 4;
  snapshot.deviceRevision = 37;
  snapshot.catalogVersion = "catalog";
  snapshot.readyMask = 63;
  snapshot.operationEpoch = operationEpoch;
  unsigned inputs = 0, completed = 0;
  std::string playedBody;
  RealtimeSessionEngine engine(audio, gptLive, [] { return true; }, [] { return 100; },
      [] { return 1800000000ull; }, [](const char*) {},
      [&](const std::string& path, const void* data, size_t size, const char*, const std::string&, unsigned sequence) {
        if (path.find("/played") != std::string::npos) playedBody.assign(static_cast<const char*>(data), size);
        else { assert(sequence == inputs); ++inputs; }
        return 204;
      }, correlation, snapshot, [&](const TurnOperationObservation&) { ++completed; return true; });
  RealtimeEvent event;
  event.type = "session"; event.streamId = "stream";
  assert(engine.Event(event));
  event.type = "listening"; event.inputId = "input";
  event.hasResponseGeneration = true;
  assert(engine.Event(event));
  const auto inputEpoch = engine.InputEpoch();
  assert(engine.SendInput(std::vector<uint8_t>(6), inputEpoch) && inputs == 1);
  event.type = "audio_cancel"; event.responseGeneration = 1;
  assert(engine.Event(event) && audio.cancelled == 1);
  event.type = "listening";
  assert(engine.Event(event) && engine.InputEpoch() == inputEpoch);
  assert(engine.SendInput(std::vector<uint8_t>(6), inputEpoch) && inputs == 2);
  event.type = "speech_stopped";
  assert(engine.Event(event));
  event.type = "started"; event.operationId = "operation";
  event.operation.operationId = event.operationId;
  event.operation.kind = "turn";
  event.operation.status = TurnOperationStatus::Completed;
  event.operation.correlation = correlation;
  event.operation.reply = "fixture reply";
  event.operation.decision = "no_selection";
  assert(engine.Event(event));
  event.type = "audio"; event.delta = "AAAAAAAA";
  assert(engine.Event(event) && audio.played == 1);
  event.type = "audio_cancel"; event.responseGeneration = 2;
  assert(engine.Event(event) && audio.cancelled == 2);
  assert(engine.Event(event) && audio.cancelled == 2);
  for (const auto* type : {"started", "audio", "completed", "listening", "visitor", "pause",
      "playback_checkpoint", "speech_started", "speech_stopped", "closed"}) {
    event.type = type; event.responseGeneration = 1;
    assert(engine.Event(event));
    event.responseGeneration = 3;
    assert(!engine.Event(event));
  }
  event.type = "audio"; event.hasResponseGeneration = false;
  assert(engine.Event(event) && audio.played == 1 && completed == 0);
  event.hasResponseGeneration = true; event.responseGeneration = 2;
  if (!gptLive) {
    event.type = "started";
    assert(engine.Event(event));
  }
  event.type = "audio";
  assert(engine.Event(event) && audio.played == 2);
  event.type = "completed";
  assert(engine.Event(event) && completed == 1 && audio.drained == 1);
  assert(playedBody == "{\"operationId\":\"operation\",\"responseGeneration\":2}");
}

void TestInputIoDoesNotOwnReaderMutex() {
  Audio audio;
  TurnCorrelation correlation; correlation.conversationId = "conversation";
  ConversationRuntimeSnapshot snapshot;
  std::promise<void> entered, release;
  auto released = release.get_future().share();
  std::mutex sentMutex;
  std::vector<std::pair<std::string, unsigned>> sent;
  RealtimeSessionEngine engine(audio, false, [] { return true; }, [] { return 100; },
      [] { return 1800000000ull; }, [](const char*) {},
      [&](const std::string&, const void*, size_t, const char*, const std::string& input, unsigned sequence) {
        bool first;
        {
          std::lock_guard<std::mutex> lock(sentMutex);
          first = sent.empty(); sent.emplace_back(input, sequence);
        }
        if (first) { entered.set_value(); released.wait(); }
        return 204;
      }, correlation, snapshot, [](const TurnOperationObservation&) { return true; });
  RealtimeEvent event; event.type = "session"; event.streamId = "stream"; assert(engine.Event(event));
  event.type = "listening"; event.inputId = "input-a"; assert(engine.Event(event));
  const auto originalEpoch = engine.InputEpoch();
  auto upload = std::async(std::launch::async, [&] { return engine.SendInput(std::vector<uint8_t>(6), originalEpoch); });
  assert(entered.get_future().wait_for(std::chrono::seconds(1)) == std::future_status::ready);
  auto reader = std::async(std::launch::async, [&] {
    RealtimeEvent next; next.type = "speech_stopped"; next.inputId = "input-a";
    if (!engine.Event(next)) return false;
    next.type = "listening"; next.inputId = "input-b";
    return engine.Event(next);
  });
  assert(reader.wait_for(std::chrono::seconds(1)) == std::future_status::ready && reader.get());
  assert(engine.InputEpoch() != originalEpoch);
  assert(!engine.SendInput(std::vector<uint8_t>(4), engine.InputEpoch())); // Sole old upload claim is still borrowed.
  release.set_value(); assert(upload.get());
  assert(engine.SendInput(std::vector<uint8_t>(4), engine.InputEpoch()));
  assert(sent.size() == 2 && sent[0] == std::make_pair(std::string("input-a"), 0u)
      && sent[1] == std::make_pair(std::string("input-b"), 0u));
}

int main() {
  TestInputIoDoesNotOwnReaderMutex();
  TestResponseReset(false);
  TestResponseReset(true);
  TestResponseReset(false, UINT64_MAX);
  TestResponseReset(true, UINT64_MAX);
  uint64_t now = 100;
  bool current = true;
  unsigned inputs = 0;
  Audio audio;
  TurnCorrelation correlation;
  correlation.conversationId = "ac665612-864c-4482-bb68-7415921119ae";
  ConversationRuntimeSnapshot snapshot;
  RealtimeSessionEngine engine(audio, false, [&]() { return current; }, [&]() { return now; },
      []() { return 1800000000ull; }, [](const char*) {},
      [&](const std::string&, const void*, size_t, const char*, const std::string&, unsigned sequence) {
        assert(sequence == 0); ++inputs; return 204;
      }, correlation, snapshot, [](const TurnOperationObservation&) { return true; });
  RealtimeEvent event;
  event.type = "session"; event.streamId = correlation.conversationId;
  assert(engine.Event(event));
  event.type = "listening"; event.inputId = correlation.conversationId;
  assert(engine.Event(event) && audio.listening);
  const uint32_t epoch = engine.InputEpoch();
  assert(engine.SendInput(std::vector<uint8_t>(6), epoch) && inputs == 1);
  event.type = "speech_stopped";
  assert(engine.Event(event) && !audio.listening);
  event.type = "listening";
  assert(engine.Event(event));
  assert(engine.SendInput(std::vector<uint8_t>(6), epoch) && inputs == 1);
  assert(engine.SendInput(std::vector<uint8_t>(6), engine.InputEpoch()) && inputs == 2);
  now += 80000;
  assert(!engine.Current() && !engine.Event(event));
  now = 100; current = false;
  assert(!engine.Current() && !engine.Event(event));
  current = true; engine.Stop();
  assert(!engine.Current() && !engine.Event(event));
  uint8_t pcm[8] = {};
  size_t size = 0;
  assert(DecodeRealtimePcm("AAAAAAAA", pcm, sizeof(pcm), size) && size == 6);
  assert(!DecodeRealtimePcm("AA?A", pcm, sizeof(pcm), size));
  assert(!DecodeRealtimePcm("AAAAAAAAAAAA", pcm, sizeof(pcm), size));
  std::cout << "Shared input epoch, wall-clock, cancellation and decoder bounds passed\n";
}

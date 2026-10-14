#include <cassert>
#include <iostream>
#include "../src/features/voice_turn/RealtimeSessionEngine.h"

struct Audio final : VoiceAudioPort {
  bool Available() const override { return true; }
  void Listen(bool enabled) override { listening = enabled; }
  bool TakeInput(std::vector<uint8_t>&) override { return true; }
  bool Play(const std::vector<uint8_t>&) override { return true; }
  bool Drain() override { return true; }
  void Stop() override {}
  bool Failed() const override { return false; }
  bool listening = false;
};
int main() {
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

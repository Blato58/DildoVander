#include <cassert>
#include <cmath>
#include <iostream>
#include "../src/features/voice_turn/PcmRateConverter.h"
#include "../src/features/voice_turn/RealtimeEventCodec.h"
#include "../src/features/local_control/LocalControlCodec.h"

int main() {
  for (const auto& rates : {std::pair<unsigned, unsigned>{24000, 32000}, {32000, 24000}}) {
    PcmRateConverter rate(rates.first, rates.second);
    std::vector<int16_t> output;
    for (unsigned i = 0; i < rates.first; ++i)
      rate.Push(static_cast<int16_t>(10000 * std::sin(2 * 3.141592653589793 * 1000 * i / rates.first)), output);
    assert(output.size() == rates.second);
    double squared = 0;
    for (unsigned i = 40; i < output.size() - 40; ++i) {
      const double expected = 10000 * std::sin(2 * 3.141592653589793 * 1000 *
          (static_cast<double>(i) / rates.second - 12.0 / rates.first));
      squared += std::pow(output[i] - expected, 2);
    }
    assert(std::sqrt(squared / (output.size() - 80)) < 6);
    rate.Reset(); output.clear();
    for (unsigned i = 0; i < 640; ++i) rate.Push(32767, output);
    for (size_t i = 40; i < output.size(); ++i) assert(output[i] > 32765);
  }
  // Anti-aliasing: a 14 kHz microphone tone must not become audible 10 kHz.
  PcmRateConverter down(32000, 24000);
  std::vector<int16_t> high;
  for (unsigned i = 0; i < 32000; ++i)
    down.Push(static_cast<int16_t>(10000 * std::sin(2 * 3.141592653589793 * 14000 * i / 32000)), high);
  double rms = 0;
  for (size_t i = 40; i < high.size(); ++i) rms += high[i] * high[i];
  assert(std::sqrt(rms / (high.size() - 40)) < 150);

  RealtimeEvent event;
  assert(ParseRealtimeEvent("{\"type\":\"heartbeat\"}", event));
  assert(ParseRealtimeEvent("{\"type\":\"audio\",\"delta\":\"AAAA\",\"responseGeneration\":0}", event));
  assert(event.hasResponseGeneration && event.responseGeneration == 0);
  assert(!ParseRealtimeEvent("{\"type\":\"audio\",\"delta\":\"AAAA\",\"responseGeneration\":true}", event));
  assert(!ParseRealtimeEvent("{\"type\":\"audio\",\"delta\":\"AAAA\",\"responseGeneration\":-1}", event));
  assert(ParseRealtimeEvent("{\"type\":\"audio_cancel\",\"responseGeneration\":2}", event));
  assert(event.responseGeneration == 2);
  assert(ParseRealtimeEvent("{\"type\":\"audio_cancel\",\"responseGeneration\":2,\"visualRevision\":3}", event));
  assert(event.hasVisualRevision && event.visualRevision == 3);
  assert(!ParseRealtimeEvent("{\"type\":\"audio_cancel\",\"responseGeneration\":2,\"visualRevision\":0}", event));
  assert(!ParseRealtimeEvent("{\"type\":\"audio_cancel\",\"responseGeneration\":2,\"visualRevision\":1000001}", event));
  assert(!ParseRealtimeEvent("{\"type\":\"audio_cancel\",\"responseGeneration\":0}", event));
  assert(!ParseRealtimeEvent("{\"type\":\"audio_cancel\",\"responseGeneration\":true}", event));
  assert(!ParseRealtimeEvent("{\"type\":\"audio_cancel\",\"responseGeneration\":2,\"extra\":0}", event));
  assert(ParseRealtimeEvent("{\"type\":\"session\",\"streamId\":\"c85743d5-1b72-41b8-8500-ae3c102b27d6\",\"audioFormat\":\"pcm16-mono-24000\"}", event));
  for (const std::string invalid : {
      "{\"type\":\"audio\",\"type\":\"heartbeat\",\"delta\":\"AAAA\"}",
      "{\"type\":\"audio\",\"delta\":\"\"}",
      "{\"type\":\"heartbeat\",\"extra\":0}",
      "{\"type\":\"session\",\"streamId\":\"bad\",\"audioFormat\":\"pcm16-mono-24000\"}",
      "{\"type\":\"completed\",\"result\":{\"reply\":\"a\",\"reply\":\"b\"}}"})
    assert(!ParseRealtimeEvent(invalid, event));
  assert(!ParseRealtimeEvent("{\"type\":\"audio\",\"delta\":\"" + std::string(43693, 'A') + "\"}", event));

  const std::string common = "\"protocolVersion\":1,\"messageId\":\"fcfaaf2b-7825-4b9e-a229-e7d8e642f705\",\"deviceId\":\"kufr-001\",\"bootId\":\"c85743d5-1b72-41b8-8500-ae3c102b27d6\"";
  LocalControlMessage message;
  std::string json = "{" + common + ",\"type\":\"voice.configure\",\"mode\":\"realtime\",\"voice\":\"cedar\"}";
  assert(ParseLocalControlMessage(json.data(), json.size(), message));
  assert(message.kind == LocalControlMessageKind::VoiceConfigure && message.voice == "cedar");
  json = "{" + common + ",\"type\":\"voice.configure\",\"mode\":\"gpt-live\",\"voice\":\"marin\"}";
  assert(ParseLocalControlMessage(json.data(), json.size(), message));
  assert(message.voiceMode == "gpt-live");
  assert(ParseRealtimeEvent("{\"type\":\"pause\"}", event));
  assert(ParseRealtimeEvent("{\"type\":\"pause\",\"responseGeneration\":1}", event));
  assert(ParseRealtimeEvent("{\"type\":\"visitor\",\"text\":\"Hello\",\"responseGeneration\":1}", event));
  assert(ParseRealtimeEvent("{\"type\":\"closed\",\"reason\":\"conversation_terminal\",\"responseGeneration\":1}", event));
  assert(ParseRealtimeEvent("{\"type\":\"speech_started\",\"inputId\":\"c85743d5-1b72-41b8-8500-ae3c102b27d6\",\"responseGeneration\":1}", event));
  assert(ParseRealtimeEvent("{\"type\":\"speech_stopped\",\"inputId\":\"c85743d5-1b72-41b8-8500-ae3c102b27d6\",\"responseGeneration\":1}", event));
  assert(!ParseRealtimeEvent("{\"type\":\"pause\",\"operationId\":\"bad\"}", event));
  assert(ParseRealtimeEvent("{\"type\":\"playback_checkpoint\",\"playbackId\":\"c85743d5-1b72-41b8-8500-ae3c102b27d6\",\"pcmBytes\":48000,\"decisionRevision\":2}", event));
  assert(event.pcmBytes == 48000 && event.decisionRevision == 2);
  assert(ParseRealtimeEvent("{\"type\":\"playback_checkpoint\",\"playbackId\":\"c85743d5-1b72-41b8-8500-ae3c102b27d6\",\"pcmBytes\":48000,\"decisionRevision\":2,\"responseGeneration\":1}", event));
  assert(!ParseRealtimeEvent("{\"type\":\"playback_checkpoint\",\"playbackId\":\"bad\",\"pcmBytes\":48000,\"decisionRevision\":2}", event));
  assert(!ParseRealtimeEvent("{\"type\":\"playback_checkpoint\",\"playbackId\":\"c85743d5-1b72-41b8-8500-ae3c102b27d6\",\"pcmBytes\":3,\"decisionRevision\":2}", event));
  json = "{" + common + ",\"type\":\"voice.start\",\"expectedRevision\":37}";
  assert(ParseLocalControlMessage(json.data(), json.size(), message));
  assert(message.kind == LocalControlMessageKind::VoiceStart && message.expectedRevision == 37);
  json = "{" + common + ",\"type\":\"voice.configure\",\"mode\":\"realtime\",\"voice\":\"onyx\"}";
  assert(!ParseLocalControlMessage(json.data(), json.size(), message));
  std::cout << "Realtime voice rate conversion, bounded events and commands passed\n";
}

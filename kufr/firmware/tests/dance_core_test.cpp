#include "../src/features/dance/DanceAssets.h"
#include <cassert>
#include <cstring>
#include <iostream>

using namespace kufr::dance;
std::array<uint8_t, 44> Wav(uint32_t frames = 24000) {
  std::array<uint8_t, 44> h{};
  std::memcpy(h.data(), "RIFF", 4); std::memcpy(h.data() + 8, "WAVEfmt ", 8);
  std::memcpy(h.data() + 36, "data", 4);
  const auto put = [&](size_t p, uint32_t n) {
    for (size_t i = 0; i < 4; ++i) h[p + i] = static_cast<uint8_t>(n >> (i * 8));
  };
  put(4, frames * 2 + 36); put(16, 16); h[20] = h[22] = 1;
  put(24, 24000); put(28, 48000); h[32] = 2; h[34] = 16; put(40, frames * 2);
  return h;
}
int main() {
  DanceManifest manifest; std::string error;
  assert(ParseDanceManifest("KUFR_DANCE_V1\nsong.wav,120,0,255,0,16,2\n", manifest, error));
  assert(manifest.count == 1 && manifest.tracks[0].bpm == 120 && manifest.tracks[0].vibrationEveryBeats == 2);
  const std::string invalid[] = {
    "KUFR_DANCE_V1\n", "KUFR_DANCE_V1\n../song.wav,120,0,1,2,3,0\n",
    "KUFR_DANCE_V1\nsong.wav,39,0,1,2,3,0\n", "KUFR_DANCE_V1\nsong.wav,241,0,1,2,3,0\n",
    "KUFR_DANCE_V1\nsong.wav,120,10001,1,2,3,0\n", "KUFR_DANCE_V1\nsong.wav,120,0,256,2,3,0\n",
    "KUFR_DANCE_V1\nsong.wav,120,0,1,2,3,17\n", "KUFR_DANCE_V1\nsong.wav,120,0,1,2,3,0,\n",
    "KUFR_DANCE_V1\nsong.wav,120,0,1,2,3,0\nSong.wav,120,0,1,2,3,0\n",
    "KUFR_DANCE_V1\nsong.wav,120,0,1,2,3,0\n\n", "KUFR_DANCE_V1\nsong.wav, 120,0,1,2,3,0\n"
  };
  for (const auto& text : invalid) {
    assert(!ParseDanceManifest(text, manifest, error)); assert(manifest.count == 0 && !error.empty());
  }
  assert(!ParseDanceManifest(std::string(2049, 'x'), manifest, error));
  assert(!IsDanceFilename("_song.wav") && !IsDanceFilename("-song.wav"));
  std::string twelve = "KUFR_DANCE_V1\n";
  for (unsigned i = 0; i < 12; ++i) twelve += "song" + std::to_string(i) + ".wav,40,0,0,255,255,16\r\n";
  assert(ParseDanceManifest(twelve, manifest, error) && manifest.count == 12);
  assert(!ParseDanceManifest(twelve + "extra.wav,120,0,0,0,0,0\n", manifest, error));
  DanceTrack track; auto header = Wav();
  assert(ParseDanceWav(header.data(), 44, 48044, track, error) && track.durationMs == 1000);
  track.offsetMs = 1000;
  assert(!ParseDanceWav(header.data(), 44, 48044, track, error)); track.offsetMs = 999;
  assert(ParseDanceWav(header.data(), 44, 48044, track, error));
  assert(!ParseDanceWav(header.data(), 43, 48044, track, error));
  assert(!ParseDanceWav(header.data(), 44, 48045, track, error));
  for (size_t offset : {size_t(20), size_t(22), size_t(24), size_t(28), size_t(32), size_t(34), size_t(36)}) {
    auto broken = header; broken[offset] ^= 1;
    assert(!ParseDanceWav(broken.data(), 44, 48044, track, error));
  }
  header = Wav(24000 * 600); track.offsetMs = 0;
  assert(ParseDanceWav(header.data(), 44, 28800044, track, error));
  header = Wav(24000 * 600 + 1);
  assert(!ParseDanceWav(header.data(), 44, 28800046, track, error));
  header = Wav(23999); assert(!ParseDanceWav(header.data(), 44, 48042, track, error));
  for (size_t count = 2; count <= 12; ++count)
    for (size_t previous = 0; previous < count; ++previous)
      for (uint32_t random = 0; random < 100; ++random) {
        const size_t selected = SelectDanceTrack(count, previous, random);
        assert(selected < count && selected != previous);
      }
  assert(SelectDanceTrack(1, 0, 123) == 0);
  DanceAudioTimeline timeline; timeline.Start(100); timeline.Submitted(960);
  assert(timeline.Position(100) == 0 && timeline.Position(110) == 10 && timeline.Position(500) == 20);
  assert(!timeline.Underrun(140) && timeline.Underrun(141));
  assert(timeline.Underrun(99));
  std::cout << "Dance manifest, canonical WAV bounds, random nonrepeat and PCM timeline tests passed.\n";
}

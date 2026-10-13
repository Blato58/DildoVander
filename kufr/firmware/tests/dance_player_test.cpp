// Compile with KUFR_DANCE_HOST_TEST: only the test build uses its private
// relative fixture directory instead of the production /sd-assets VFS mount.
#include "sd_stubs/SdPlatform.h"
#include "../src/features/dance/EspIdfDancePlayer.h"
#include "../src/features/voice_turn/VoiceOutputCancellation.h"
#include <cassert>
#include <cstdio>
#include <fstream>
#include <functional>
#include <iostream>
#include <sys/stat.h>
#if defined(_WIN32)
#include <direct.h>
#else
#include <unistd.h>
#endif
namespace ota_fake {
uint64_t nowMs = 0;
std::map<std::string, std::string> settings;
}
namespace sd_fake {
bool mountSuccess = true, unmountSuccess = true, mounted = false;
unsigned mounts = 0, unmounts = 0, cleanups = 0, transactions = 0;
sdmmc_host_t configuredHost;
sdmmc_slot_config_t configuredSlot;
sdmmc_card_t card;
}
namespace dance_fake { uint32_t random = 0; }
namespace kufr { namespace assets {
struct SdAssetsTestAccess { static void Run(EspIdfSdAssets& sd) { sd.Execute(sd.active_); } };
} namespace dance {
struct DancePlayerTestAccess {
  static uint32_t Epoch(EspIdfDancePlayer& player) { return player.cancellationEpoch_.load(); }
  static void Run(EspIdfDancePlayer& player, uint32_t epoch) { player.Execute(epoch); }
};
} }
using namespace kufr::dance;
struct Audio final : VoiceAudioPort {
  bool active = false, failed = false, available = true, failPlay = false;
  unsigned plays = 0, begins = 0, stops = 0, drains = 0;
  size_t playedBytes = 0, largest = 0;
  uint64_t playDelay = 19; // Together with the worker's one fake tick: 20 ms/block.
  std::function<void()> afterBegin, afterPlay;
  bool Available() const override { return available; }
  void Begin() override { ++begins; active = true; if (afterBegin) afterBegin(); }
  void Listen(bool enabled) override { assert(!enabled); }
  bool TakeInput(std::vector<uint8_t>&) override { assert(false); return false; }
  bool Play(const std::vector<uint8_t>& pcm) override {
    assert(active); ++plays; largest = std::max(largest, pcm.size()); playedBytes += pcm.size();
    ota_fake::nowMs += playDelay;
    if (afterPlay) afterPlay();
    return !failPlay && active;
  }
  bool Drain() override { ++drains; return active; }
  void Stop() override { ++stops; active = false; }
  bool Failed() const override { return failed; }
};
void WriteSong(const char* name, bool valid = true) {
  std::vector<uint8_t> bytes(48044);
  std::memcpy(bytes.data(), "RIFF", 4); std::memcpy(bytes.data() + 8, "WAVEfmt ", 8);
  std::memcpy(bytes.data() + 36, "data", 4);
  const auto put = [&](size_t p, uint32_t value) {
    for (size_t i = 0; i < 4; ++i) bytes[p + i] = static_cast<uint8_t>(value >> (i * 8));
  };
  put(4, 48036); put(16, 16); bytes[20] = bytes[22] = 1;
  put(24, valid ? 24000 : 44100); put(28, 48000); bytes[32] = 2; bytes[34] = 16; put(40, 48000);
  std::ofstream file(std::string("dance-test-assets/") + name, std::ios::binary);
  file.write(reinterpret_cast<const char*>(bytes.data()), bytes.size());
}
void Config(const std::string& text) {
  std::ofstream file("dance-test-assets/dance.csv", std::ios::binary); file << text;
}
int main() {
#if defined(_WIN32)
  assert(_mkdir("dance-test-assets") == 0);
#else
  assert(mkdir("dance-test-assets", 0700) == 0);
#endif
  WriteSong("one.wav"); WriteSong("two.wav");
  Config("KUFR_DANCE_V1\none.wav,120,0,255,0,0,0\ntwo.wav,120,0,0,255,0,0\n");
  ota_fake::settings = {{"profile_b_ok", "1"}, {"sd_verified", "1"}};
  kufr::assets::EspIdfSdAssets assets; assert(assets.Begin());
  Audio audio; EspIdfDancePlayer player(assets, audio);
  assert(player.Begin() && !player.Status().available);
  assert(!player.Start("before-mount"));
  assets.Tick(true); kufr::assets::SdAssetsTestAccess::Run(assets);
  assert(player.Status().available && player.Status().phase == "idle");
  assert(player.Start("first") && assets.PlaybackReserved());
  assert(!player.Start("first") && !player.Start("overlap"));
  DancePlayerTestAccess::Run(player, DancePlayerTestAccess::Epoch(player));
  assert(player.Status().phase == "completed" && player.Status().positionMs == 1000);
  assert(audio.plays == 50 && audio.playedBytes == 48000 && audio.largest == 960 && audio.drains == 1);
  assert(!assets.Reserved() && !player.Start("first"));
  const std::string first = player.Status().track;
  assert(player.Start("second")); DancePlayerTestAccess::Run(player, DancePlayerTestAccess::Epoch(player));
  assert(player.Status().track != first);
  // Network loss repeatedly cancels inactive voice; it must not touch the
  // audio gate currently owned by the SD worker. Use the backend's real helper.
  std::atomic<uint64_t> voiceEpoch{0};
  std::atomic<bool> voicePending{false}, voiceBusy{false};
  audio.afterPlay = [&] { CancelVoiceOutput(voiceEpoch, voicePending, voiceBusy, audio); };
  const size_t beforeOffline = audio.playedBytes;
  assert(player.Start("offline")); DancePlayerTestAccess::Run(player, DancePlayerTestAccess::Epoch(player));
  assert(player.Status().phase == "completed" && audio.playedBytes == beforeOffline + 48000);
  assert(voiceEpoch.load() == 50 && !assets.Reserved());
  audio.afterPlay = nullptr;
  // Active and queued voice still close their own gate; active work remains
  // busy until its worker finishes cleanup, queued work may release afterward.
  audio.Begin(); voiceBusy.store(true);
  CancelVoiceOutput(voiceEpoch, voicePending, voiceBusy, audio);
  assert(!audio.active && voiceBusy.load());
  audio.Begin(); voicePending.store(true);
  CancelVoiceOutput(voiceEpoch, voicePending, voiceBusy, audio);
  assert(!audio.active && !voicePending.load() && !voiceBusy.load());
  const unsigned priorPlays = audio.plays;
  audio.afterBegin = [&] { player.Stop(); };
  assert(player.Start("begin-race")); DancePlayerTestAccess::Run(player, DancePlayerTestAccess::Epoch(player));
  assert(player.Status().phase == "idle" && audio.plays == priorPlays && !audio.active && !assets.Reserved());
  audio.afterBegin = nullptr; audio.afterPlay = [&] { player.Stop(); };
  assert(player.Start("play-race")); DancePlayerTestAccess::Run(player, DancePlayerTestAccess::Epoch(player));
  assert(player.Status().phase == "idle" && audio.plays == priorPlays + 1 && !assets.Reserved());
  audio.afterPlay = nullptr;
  const unsigned stops = audio.stops; player.Stop(); assert(audio.stops == stops);
  assert(player.Start("cancel-before")); const uint32_t epoch = DancePlayerTestAccess::Epoch(player);
  player.Stop(); assert(player.Status().phase == "stopping" && assets.Reserved());
  DancePlayerTestAccess::Run(player, epoch);
  assert(player.Status().phase == "idle" && !assets.Reserved());
  // The invalid second entry prevents even the valid first song from playing.
  WriteSong("two.wav", false);
  assert(player.Start("bad-track")); DancePlayerTestAccess::Run(player, DancePlayerTestAccess::Epoch(player));
  assert(player.Status().phase == "failed" && player.Status().error == "dance_track_invalid" && !assets.Reserved());
  WriteSong("two.wav"); audio.failPlay = true;
  assert(player.Start("audio-fail")); DancePlayerTestAccess::Run(player, DancePlayerTestAccess::Epoch(player));
  assert(player.Status().error == "dance_audio_failed" && !assets.Reserved()); audio.failPlay = false;
  audio.playDelay = 300;
  assert(player.Start("stall")); DancePlayerTestAccess::Run(player, DancePlayerTestAccess::Epoch(player));
  assert(player.Status().error == "dance_audio_underrun" && !assets.Reserved()); audio.playDelay = 19;
  Config("KUFR_DANCE_V1\none.wav,120,0,255,0,0,0\nbad\n");
  assert(player.Start("bad-config")); DancePlayerTestAccess::Run(player, DancePlayerTestAccess::Epoch(player));
  assert(player.Status().error == "dance_manifest_invalid" && !assets.Reserved());
  for (unsigned i = 10; i < 128; ++i) {
    assert(player.Start("history-" + std::to_string(i)));
    DancePlayerTestAccess::Run(player, DancePlayerTestAccess::Epoch(player));
  }
  assert(!player.Start("history-full") && player.Status().error == "dance_request_history_full");
  std::remove("dance-test-assets/one.wav"); std::remove("dance-test-assets/two.wav"); std::remove("dance-test-assets/dance.csv");
#if defined(_WIN32)
  assert(_rmdir("dance-test-assets") == 0);
#else
  assert(rmdir("dance-test-assets") == 0);
#endif
  std::cout << "Dance bounded stream, complete manifest validation, nonrepeat, replay, Begin/Play cancellation, stall and SD release tests passed.\n";
}

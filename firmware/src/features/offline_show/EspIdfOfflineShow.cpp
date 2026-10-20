#include "EspIdfOfflineShow.h"
#include <cstdio>
#include <algorithm>
#include <cstring>
#include <vector>
#include "../provisioning/EspIdfSha256.h"
#include "ImaAdpcm.h"
#include "../voice_turn/PcmRateConverter.h"

namespace kufr { namespace offline {
bool EspIdfOfflineAudio::Begin() {
  const auto& bank = CompiledBank();
  if (task_ || bank.totalBytes > bank.capacityBytes || !audio_.Available()) return false;
  EspIdfSha256 hasher;
  for (size_t index = 0; bank.complete && index < bank.count; ++index) {
    const auto& clip = bank.clips[index];
    if (!clip.pcm || !clip.bytes || clip.bytes > 384000 || clip.durationMs > 8000) return false;
    if (clip.format == ClipFormat::ImaAdpcm4) {
      ImaAdpcm decoder;
      if (!decoder.Begin(clip.pcm, clip.bytes, clip.samples)) return false;
    } else if (clip.format != ClipFormat::PcmS16Le || clip.bytes % 2) return false;
    std::array<uint8_t, 32> digest{};
    if (!hasher.Hash(clip.pcm, clip.bytes, digest)) return false;
    char hex[65]{};
    for (size_t index = 0; index < 32; ++index) std::snprintf(hex + index * 2, 3, "%02x", digest[index]);
    if (std::strcmp(hex, clip.sha256)) return false;
  }
  // Existing Play owns a 5.5 KiB conversion frame on its caller's stack.
  ready_ = xTaskCreate(Worker, "offline_audio", 12288, this, 5, &task_) == pdPASS;
  return ready_;
}
bool EspIdfOfflineAudio::Play(const Clip& clip, uint64_t) {
  if (clip.durationMs > 8000 || (clip.bytes % 2 == 1 && clip.format != ClipFormat::ImaAdpcm4) ||
      (clip.format == ClipFormat::PcmS16Le32k && clip.bytes > 512000)) return false;
  if (!Available() || !clip.pcm || !clip.bytes || busy_.exchange(true)) return false;
  failed_.store(false);
  pending_ = &clip;
  requestedEpoch_ = epoch_.load();
  xTaskNotifyGive(task_);
  return true;
}
void EspIdfOfflineAudio::Cancel() {
  std::lock_guard<std::mutex> lock(startGate_);
  if (busy_.load()) { epoch_.fetch_add(1); audio_.Stop(); }
}
void EspIdfOfflineAudio::Worker(void* context) { static_cast<EspIdfOfflineAudio*>(context)->Run(); }
void EspIdfOfflineAudio::Run() {
  std::vector<uint8_t> frame;
  frame.reserve(960);
  std::vector<int16_t> converted;
  converted.reserve(480);
  PcmRateConverter converter(32000, 24000);
  for (;;) {
    ulTaskNotifyTake(pdTRUE, portMAX_DELAY);
    const uint32_t epoch = requestedEpoch_.load();
    const Clip* clip = pending_.load();
    bool success = clip != nullptr;
    ImaAdpcm decoder;
    const bool compressed = clip && clip->format == ClipFormat::ImaAdpcm4;
    const bool source32k = clip && clip->format == ClipFormat::PcmS16Le32k;
    converter.Reset();
    if (compressed) success = decoder.Begin(clip->pcm, clip->bytes, clip->samples);
    {
      std::lock_guard<std::mutex> lock(startGate_);
      success = success && epoch == epoch_.load();
      if (success) {
        audio_.Begin();
        audio_.Listen(false); // No deferred capture queue or guest recording offline.
      }
    }
    const size_t decodedBytes = compressed ? clip->samples * 2 : clip ? clip->bytes : 0;
    const size_t blockBytes = source32k ? 1280 : 960;
    for (size_t offset = 0; success && offset < decodedBytes; offset += blockBytes) {
      if (epoch != epoch_.load()) { success = false; break; }
      const size_t bytes = std::min(blockBytes, decodedBytes - offset);
      if (compressed) {
        frame.resize(bytes);
        if (decoder.Read(frame.data(), bytes) != bytes) { success = false; break; }
      } else if (source32k) {
        converted.clear();
        for (size_t index = offset; index < offset + bytes; index += 2)
          converter.Push(static_cast<int16_t>(clip->pcm[index] | (clip->pcm[index + 1] << 8)), converted);
        frame.clear();
        for (int16_t sample : converted) {
          frame.push_back(static_cast<uint8_t>(sample));
          frame.push_back(static_cast<uint8_t>(sample >> 8));
        }
      } else frame.assign(clip->pcm + offset, clip->pcm + offset + bytes);
      success = audio_.Play(frame) && !audio_.Failed();
    }
    success = success && epoch == epoch_.load() && audio_.Drain();
    if (!success) {
      std::lock_guard<std::mutex> lock(startGate_);
      if (epoch == epoch_.load()) audio_.Stop();
    }
    failed_.store(!success);
    busy_.store(false);
  }
}
} }

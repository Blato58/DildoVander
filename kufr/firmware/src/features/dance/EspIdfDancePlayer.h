#pragma once
#include <array>
#include <atomic>
#include "DanceAssets.h"
#include "../sd_assets/EspIdfSdAssets.h"
#include "../voice_turn/VoiceAudioPort.h"

namespace kufr { namespace dance {
class EspIdfDancePlayer final : public DancePlayerPort {
 public:
  EspIdfDancePlayer(assets::EspIdfSdAssets& assets, VoiceAudioPort& audio) : assets_(assets), audio_(audio) {}
  bool Begin();
  DanceStatus Status() const override;
  bool Start(const std::string& requestId) override;
  void Stop() override;
 private:
  friend struct DancePlayerTestAccess;
  static void WorkerEntry(void* owner);
  void Worker();
  void Execute(uint32_t epoch);
  bool Cancelled(uint32_t epoch) const { return cancellationEpoch_.load() != epoch; }
  bool LoadManifest(DanceManifest& manifest, uint32_t epoch, std::string& error);
  bool PlayTrack(const DanceTrack& track, uint32_t epoch, std::string& error);
  void Finish(uint32_t epoch, const std::string& error);
  assets::EspIdfSdAssets& assets_;
  VoiceAudioPort& audio_;
  mutable SemaphoreHandle_t mutex_ = nullptr;
  TaskHandle_t task_ = nullptr;
  DanceStatus status_;
  std::atomic<uint32_t> cancellationEpoch_{0};
  std::array<std::string, 128> acceptedRequests_;
  size_t acceptedCount_ = 0;
  std::string previousTrack_;
};
} }

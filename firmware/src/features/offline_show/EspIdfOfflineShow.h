#pragma once
#include <atomic>
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "EspIdfOfflineOutboxStorage.h"
#include "../voice_turn/VoiceAudioPort.h"

namespace kufr { namespace offline {
// One bounded worker streams bank or owned online PCM through the existing owner.
// The control owner retains each requested Clip and its bytes until Busy clears.
class EspIdfOfflineAudio final : public AudioPort {
 public:
  explicit EspIdfOfflineAudio(VoiceAudioPort& audio) : audio_(audio) {}
  bool Begin();
  bool Available() const override { return ready_ && audio_.Available(); }
  bool Play(const Clip& clip, uint64_t epoch) override;
  void Cancel() override;
  bool Busy() const override { return busy_.load(); }
  bool Failed() const override { return failed_.load(); }
 private:
  static void Worker(void* context);
  void Run();
  VoiceAudioPort& audio_;
  TaskHandle_t task_ = nullptr;
  std::atomic<const Clip*> pending_{nullptr};
  std::atomic<uint32_t> epoch_{0};
  std::atomic<uint32_t> requestedEpoch_{0};
  std::mutex startGate_;
  std::atomic<bool> busy_{false}, failed_{false};
  bool ready_ = false;
};
} }

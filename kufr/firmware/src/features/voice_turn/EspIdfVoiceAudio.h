#pragma once
#include <atomic>
#include <vector>
#include "driver/i2s_std.h"
#include "freertos/FreeRTOS.h"
#include "freertos/queue.h"
#include "freertos/task.h"
#include "PcmRateConverter.h"
#include "VoiceAudioPort.h"

class EspIdfVoiceAudio final : public VoiceAudioPort {
 public:
  bool Initialize(bool nvsInitialized);
  void Continuous(bool enabled) { continuous_.store(enabled); }
  void Begin() override;
  bool Available() const override { return available_; }
  void Listen(bool enabled) override;
  bool TakeInput(std::vector<uint8_t>& pcm) override;
  bool Play(const std::vector<uint8_t>& pcm) override;
  bool Drain() override;
  void Stop() override;
  bool Failed() const override { return failed_.load(); }
 private:
  static void CaptureTask(void* argument);
  void Capture();
  struct Frame { uint32_t epoch; uint16_t bytes; uint8_t pcm[960]; };
  i2s_chan_handle_t tx_ = nullptr, rx_ = nullptr;
  QueueHandle_t input_ = nullptr;
  std::atomic<bool> continuous_{false};
  std::atomic<bool> active_{false}, listening_{false}, failed_{false}, playing_{false};
  std::atomic<uint32_t> epoch_{0};
  bool available_ = false;
  portMUX_TYPE muteGate_ = portMUX_INITIALIZER_UNLOCKED;
  PcmRateConverter outputRate_{24000, 32000};
};

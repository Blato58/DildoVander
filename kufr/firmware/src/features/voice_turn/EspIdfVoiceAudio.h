#pragma once
#include <atomic>
#include <array>
#include <vector>
#include <mutex>
#include "driver/i2s_std.h"
#include "freertos/FreeRTOS.h"
#include "freertos/queue.h"
#include "freertos/task.h"
#include "PcmRateConverter.h"
#include "VoiceAudioPort.h"
#include "AudioBoardPort.h"
#include "../physical_effects/PresentationAudioPort.h"

class EspIdfVoiceAudio final : public VoiceAudioPort, public PresentationAudioPort {
 public:
  bool Initialize(bool nvsInitialized, AudioBoardPort* board = nullptr);
  void Continuous(bool enabled) { continuous_.store(enabled); }
  void Begin() override;
  bool Available() const override { return available_; }
  void Listen(bool enabled) override;
  bool TakeInput(std::vector<uint8_t>& pcm) override;
  bool Play(const std::vector<uint8_t>& pcm) override;
  bool Drain() override;
  void Stop() override;
  bool Failed() const override { return failed_.load(); }
  void Cue(uint16_t frequencyHz, uint16_t durationMs) override;
  void StopCues() override;
 private:
  static void CaptureTask(void* argument);
  void Capture();
  void PumpCue();
  bool Mute(bool enabled);
  struct Frame { uint32_t epoch; uint16_t bytes; uint8_t pcm[960]; };
  i2s_chan_handle_t tx_ = nullptr, rx_ = nullptr;
  QueueHandle_t input_ = nullptr;
  std::atomic<bool> continuous_{false};
  std::atomic<bool> active_{false}, listening_{false}, failed_{false}, playing_{false};
  std::atomic<uint32_t> epoch_{0};
  bool available_ = false;
  std::mutex muteGate_;
  // Cue writes and voice DMA writes share a short critical section. Scheduling
  // from the control task only updates atomics; synthesis runs on capture task.
  std::mutex outputGate_;
  std::atomic<uint32_t> cue_{0};
  uint32_t cueSample_ = 0;
  std::array<int32_t, 1280> cuePcm_{}; // Capture-owned; never double the task stack.
  AudioBoardPort* board_ = nullptr;
  PcmRateConverter outputRate_{24000, 32000};
};

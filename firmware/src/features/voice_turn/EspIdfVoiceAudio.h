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
#include "BoundedPlaybackOwner.h"
#include "AudioBoardPort.h"
#include "../physical_effects/PresentationAudioPort.h"

class EspIdfVoiceAudio final : public VoiceAudioPort, public PresentationAudioPort, public BoundedPlaybackOwner::Port {
 public:
  bool Initialize(bool nvsInitialized, AudioBoardPort* board = nullptr);
  void Continuous(bool enabled) { continuous_.store(enabled); }
  // Async reader capture request: never changes TX epoch, mute or DMA state.
  void SetCaptureEnabled(bool enabled);
  bool SetCaptureEnabled(uint64_t expectedSession, bool enabled);
  bool TakeInput(uint64_t expectedSession, std::vector<uint8_t>& pcm);
  using Fence = BoundedPlaybackOwner::Fence;
  void Begin() override;
  bool Begin(const Fence& fence) override;
  bool Available() const override { return available_; }
  void Listen(bool enabled) override;
  bool TakeInput(std::vector<uint8_t>& pcm) override;
  bool Play(const std::vector<uint8_t>& pcm) override;
  bool Play(const Fence& fence, const uint8_t* pcm, size_t length) override;
  bool Drain() override;
  bool Drain(const Fence& fence) override;
  void Stop() override;
  bool CancelPlayback() override;
  bool CancelPlayback(const Fence& fence) override;
  // Immediate software fence only; preserves session/capture ownership. Profile
  // B has no GPIO mute, so admitted DMA remains a hardware latency boundary.
  bool MutePlayback();
  bool Failed() const override { return failed_.load(); }
  void Cue(uint16_t frequencyHz, uint16_t durationMs) override;
  CueResult TryCue(uint16_t frequencyHz, uint16_t durationMs) override;
  void StopCues() override;
 private:
  static void CaptureTask(void* argument);
  void Capture();
  void PumpCue();
  bool Mute(bool enabled);
  uint32_t StopLocked();
  bool BeginPlayback(const Fence* fence);
  bool PlayPlayback(const Fence* fence, const uint8_t* pcm, size_t length);
  bool DrainPlayback(const Fence* fence);
  bool CancelPlayback(const Fence* fence);
  bool PlaybackCurrent(const Fence* fence, uint32_t epoch, bool requirePlaying) const;
  bool PlaybackFault(const Fence* fence, uint32_t epoch);
  bool MarkOutputLocked(bool unsafe);
  void SetCaptureLocked(bool enabled);
  bool TakeInputOwned(uint64_t expectedSession, std::vector<uint8_t>& pcm);
  struct Frame { uint32_t epoch; uint16_t bytes; uint8_t pcm[960]; };
  i2s_chan_handle_t tx_ = nullptr, rx_ = nullptr;
  QueueHandle_t input_ = nullptr;
  std::atomic<bool> continuous_{false};
  std::atomic<bool> active_{false}, listening_{false}, failed_{false}, playing_{false};
  std::atomic<uint32_t> epoch_{0};
  std::atomic<uint32_t> captureEpoch_{0};
  uint64_t captureSession_ = 0; // muteGate_ binds the reader to raw Begin's scope.
  std::atomic<uint64_t> outputTaint_{0};
  std::atomic<bool> outputUnsafe_{false};
  bool available_ = false;
  std::mutex muteGate_;
  // Worker DMA and optional cue writes serialize across their bounded IO wait.
  // Stop/MutePlayback and reader Listen never acquire this output lock.
  std::mutex outputGate_;
  std::atomic<uint32_t> cue_{0};
  // Producer uses try-acquire; an in-flight capture write cannot accept a new
  // identically packed cue and later overwrite it through an ABA completion.
  std::atomic_flag cueGate_ = ATOMIC_FLAG_INIT;
  uint32_t cueSample_ = 0;
  std::array<int32_t, 1280> cuePcm_{}; // Capture-owned; never double the task stack.
  AudioBoardPort* board_ = nullptr;
  PcmRateConverter outputRate_{24000, 32000};
};

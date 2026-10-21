#include "EspIdfVoiceAudio.h"
#include <array>
#include <cstring>
#include <cmath>
#include "driver/gpio.h"
#include "nvs.h"
#include "../physical_effects/BoardPins.h"


bool EspIdfVoiceAudio::Mute(bool enabled) {
  // B keeps SD unmodified. Stop/listen close the PCM gate, queued DMA
  // drains into auto-cleared silence; this is not immediate hardware mute.
  const bool result = board_ ? board_->AudioReady() :
                               gpio_set_level(GPIO_NUM_16, enabled ? 1 : 0) == ESP_OK;
  if (!result) failed_.store(true);
  return result;
}

bool EspIdfVoiceAudio::Initialize(bool nvsInitialized, AudioBoardPort* board) {
  if (board) {
    if (!nvsInitialized || !board->AudioReady()) return false;
    board_ = board;
  } else {
    nvs_handle_t handle = 0;
    uint8_t verified = 0;
    if (!nvsInitialized || nvs_open("wand_audio", NVS_READONLY, &handle) != ESP_OK) return false;
    const bool wired = nvs_get_u8(handle, "profile_a_ok", &verified) == ESP_OK && verified == 1;
    nvs_close(handle);
    if (!wired || gpio_set_direction(GPIO_NUM_16, GPIO_MODE_OUTPUT) != ESP_OK) return false;
  }
  if (!Mute(false)) return false;
  i2s_chan_config_t channels = I2S_CHANNEL_DEFAULT_CONFIG(I2S_NUM_AUTO, I2S_ROLE_MASTER);
  channels.dma_desc_num = 8;
  channels.dma_frame_num = 320;
  channels.auto_clear = true;
  if (i2s_new_channel(&channels, &tx_, &rx_) != ESP_OK) return false;
  i2s_std_config_t standard = {};
  standard.clk_cfg = I2S_STD_CLK_DEFAULT_CONFIG(32000);
  standard.slot_cfg = I2S_STD_PHILIPS_SLOT_DEFAULT_CONFIG(I2S_DATA_BIT_WIDTH_32BIT, I2S_SLOT_MODE_STEREO);
  standard.gpio_cfg.mclk = I2S_GPIO_UNUSED;
  const auto& pins = board_ ? KufrBoard::ProfileB : KufrBoard::ProfileA;
  standard.gpio_cfg.bclk = static_cast<gpio_num_t>(pins.bclk);
  standard.gpio_cfg.ws = static_cast<gpio_num_t>(pins.ws);
  standard.gpio_cfg.dout = static_cast<gpio_num_t>(pins.dout);
  standard.gpio_cfg.din = static_cast<gpio_num_t>(pins.din);
  if (i2s_channel_init_std_mode(tx_, &standard) != ESP_OK ||
      i2s_channel_init_std_mode(rx_, &standard) != ESP_OK ||
      i2s_channel_enable(tx_) != ESP_OK || i2s_channel_enable(rx_) != ESP_OK) return false;
  input_ = xQueueCreate(20, sizeof(Frame)); // 400 ms; overflow fails the session.
  TaskHandle_t task = nullptr;
  if (!input_ || xTaskCreate(CaptureTask, "voice_capture", 8192, this, 3, &task) != pdPASS) return false;
  available_ = true;
  return true;
}

namespace {
bool Allowed(const BoundedPlaybackOwner::Fence* fence) { return !fence || fence->Current(); }
void ScrubBytes(void* data, size_t size) {
  auto* bytes = static_cast<volatile uint8_t*>(data);
  for (size_t i = 0; i != size; ++i) bytes[i] = 0;
}
struct PlaybackScratch {
  PlaybackScratch() { samples.reserve(684); }
  ~PlaybackScratch() {
    if (samples.data()) ScrubBytes(samples.data(), samples.capacity() * sizeof(int16_t));
    ScrubBytes(slots.data(), sizeof(slots));
  }
  std::vector<int16_t> samples;
  std::array<int32_t, 1368> slots{};
};
}

void EspIdfVoiceAudio::Begin() { BeginPlayback(nullptr); }
bool EspIdfVoiceAudio::Begin(const Fence& fence) { return BeginPlayback(&fence); }
uint32_t EspIdfVoiceAudio::StopLocked() {
  active_.store(false);
  listening_.store(false);
  playing_.store(false);
  captureSession_ = 0;
  captureEpoch_.fetch_add(1);
  MarkOutputLocked(true);
  const uint32_t epoch = epoch_.fetch_add(1) + 1;
  if (available_) Mute(false);
  return epoch;
}
bool EspIdfVoiceAudio::BeginPlayback(const Fence* fence) {
  StopCues();
  uint32_t epoch;
  {
    std::lock_guard<std::mutex> muteLock(muteGate_);
    if (!available_ || !Allowed(fence)) return false;
    epoch = StopLocked();
  }
  // A cue/older admitted DMA write may hold this lock for its bounded IO wait.
  // Stop never waits on it. Recheck both fences after that wait before revival.
  std::lock_guard<std::mutex> outputLock(outputGate_);
  {
    std::lock_guard<std::mutex> muteLock(muteGate_);
    if (epoch_.load() != epoch || !Allowed(fence)) return false;
    if ((board_ && !board_->AudioReady()) || !Mute(false)) { failed_.store(true); return false; }
  }
  // A previous cue/session can leave queued profile-B PCM despite software
  // mute. Flush on the worker before any new capture lease becomes available.
  // Stop can invalidate every bounded write without waiting for outputGate_.
  std::array<int32_t, 640> silence{};
  for (int i = 0; i != 9; ++i) {
    { std::lock_guard<std::mutex> muteLock(muteGate_);
      if (epoch_.load() != epoch || !Allowed(fence)) return false; }
    size_t written = 0;
    if (i2s_channel_write(tx_, silence.data(), sizeof(silence), &written, 100) != ESP_OK || written != sizeof(silence))
      return PlaybackFault(fence, epoch);
    { std::lock_guard<std::mutex> muteLock(muteGate_);
      if (epoch_.load() != epoch || !Allowed(fence)) return false; }
  }
  std::lock_guard<std::mutex> muteLock(muteGate_);
  if (epoch_.load() != epoch || !Allowed(fence) || !MarkOutputLocked(false)) return false;
  failed_.store(false);
  if (input_) xQueueReset(input_);
  outputRate_.Reset();
  captureSession_ = fence ? fence->scope.session : 0;
  active_.store(true);
  return true;
}

void EspIdfVoiceAudio::Listen(bool enabled) {
  bool muted;
  {
    std::lock_guard<std::mutex> muteLock(muteGate_);
    if (captureSession_) return; // A legacy reader cannot alter a scoped TX owner.
    listening_.store(false);
    muted = Mute(false);
    if (input_) xQueueReset(input_);
    if (!muted) { StopCues(); StopLocked(); return; }
    if (muted) {
      epoch_.fetch_add(1);
      captureEpoch_.fetch_add(1);
      if (enabled) {
        failed_.store(false);
        playing_.store(false);
        listening_.store(active_.load());
      }
    }
  }
  // outputRate_ belongs exclusively to the playback worker. First Play resets
  // it; reader-side capture changes must not race that worker's FIR history.
}

void EspIdfVoiceAudio::SetCaptureEnabled(bool enabled) {
  std::lock_guard<std::mutex> muteLock(muteGate_);
  if (captureSession_) return; // Legacy cleanup cannot alter a scoped reader.
  SetCaptureLocked(enabled);
}
bool EspIdfVoiceAudio::SetCaptureEnabled(uint64_t expectedSession, bool enabled) {
  std::lock_guard<std::mutex> muteLock(muteGate_);
  if (!expectedSession || captureSession_ != expectedSession) return false;
  SetCaptureLocked(enabled);
  return true;
}
void EspIdfVoiceAudio::SetCaptureLocked(bool enabled) {
  const bool desired = enabled && active_.load() && !failed_.load();
  if (listening_.exchange(desired) != desired) {
    captureEpoch_.fetch_add(1);
    if (input_) xQueueReset(input_);
  }
}

bool EspIdfVoiceAudio::TakeInput(uint64_t expectedSession, std::vector<uint8_t>& pcm) {
  if (!expectedSession) { pcm.clear(); return false; }
  return TakeInputOwned(expectedSession, pcm);
}
bool EspIdfVoiceAudio::TakeInputOwned(uint64_t expectedSession, std::vector<uint8_t>& pcm) {
  pcm.clear();
  pcm.reserve(5 * 960); // Allocate outside the Stop/mute metadata gate.
  Frame frame{};
  for (unsigned i = 0; i != 5; ++i) {
    bool popped = false;
    {
      std::lock_guard<std::mutex> muteLock(muteGate_);
      if (captureSession_ != expectedSession || failed_.load()) {
        ScrubBytes(frame.pcm, sizeof(frame.pcm));
        std::fill(pcm.begin(), pcm.end(), 0); pcm.clear();
        return false;
      }
      if (!listening_.load()) break;
      popped = xQueueReceive(input_, &frame, 0) == pdTRUE;
      if (popped && (frame.epoch != captureEpoch_.load() || frame.bytes > sizeof(frame.pcm))) frame.bytes = 0;
    }
    if (!popped) break;
    pcm.insert(pcm.end(), frame.pcm, frame.pcm + frame.bytes);
    ScrubBytes(frame.pcm, sizeof(frame.pcm));
  }
  return !failed_.load();
}

bool EspIdfVoiceAudio::MarkOutputLocked(bool unsafe) {
  const auto previous = outputTaint_.load();
  if (previous == UINT64_MAX) {
    failed_.store(true); active_.store(false); listening_.store(false);
    return false;
  }
  outputUnsafe_.store(unsafe);
  outputTaint_.store(previous + 1);
  return true;
}

void EspIdfVoiceAudio::Stop() {
  StopCues();
  std::lock_guard<std::mutex> muteLock(muteGate_);
  StopLocked();
}
bool EspIdfVoiceAudio::MutePlayback() {
  std::lock_guard<std::mutex> muteLock(muteGate_);
  playing_.store(false);
  MarkOutputLocked(true);
  epoch_.fetch_add(1);
  return available_ && Mute(false);
}
bool EspIdfVoiceAudio::PlaybackCurrent(const Fence* fence, uint32_t epoch, bool requirePlaying) const {
  return active_.load() && !failed_.load() && epoch_.load() == epoch &&
      (!requirePlaying || playing_.load()) && Allowed(fence);
}
bool EspIdfVoiceAudio::PlaybackFault(const Fence* fence, uint32_t epoch) {
  std::lock_guard<std::mutex> muteLock(muteGate_);
  if (epoch_.load() == epoch && Allowed(fence)) {
    failed_.store(true);
    StopCues(); StopLocked();
  }
  return false;
}

bool EspIdfVoiceAudio::TakeInput(std::vector<uint8_t>& pcm) {
  const bool result = TakeInputOwned(0, pcm);
  // The legacy upload loop expects an empty read to yield. Never wait while
  // holding the capture ownership gate or borrowing a successor's queue.
  if (result && pcm.empty()) vTaskDelay(pdMS_TO_TICKS(10));
  return result;
}

bool EspIdfVoiceAudio::CancelPlayback() { return CancelPlayback(static_cast<const Fence*>(nullptr)); }
bool EspIdfVoiceAudio::CancelPlayback(const Fence& fence) { return CancelPlayback(&fence); }
bool EspIdfVoiceAudio::CancelPlayback(const Fence* fence) {
  uint32_t epoch;
  {
    std::lock_guard<std::mutex> muteLock(muteGate_);
    if (!active_.load() || failed_.load() || !Allowed(fence)) return false;
    playing_.store(false);
    if (!MarkOutputLocked(true)) return false;
    epoch = epoch_.fetch_add(1) + 1;
    if (!Mute(false)) return false;
  }
  // Worker-owned flush. Profile B's admitted DMA tail cannot mute instantly;
  // the immediate fence prevents future unmute, and these writes clear the tail.
  std::array<int32_t, 640> silence{};
  for (int i = 0; i < 9; ++i) {
    std::lock_guard<std::mutex> outputLock(outputGate_);
    {
      std::lock_guard<std::mutex> muteLock(muteGate_);
      if (!PlaybackCurrent(fence, epoch, false)) return false;
    }
    size_t written = 0;
    const bool wrote = i2s_channel_write(tx_, silence.data(), sizeof(silence), &written, 100) == ESP_OK &&
        written == sizeof(silence);
    if (!wrote) return PlaybackFault(fence, epoch);
    std::lock_guard<std::mutex> muteLock(muteGate_);
    if (!PlaybackCurrent(fence, epoch, false)) return false;
  }
  std::lock_guard<std::mutex> muteLock(muteGate_);
  if (!PlaybackCurrent(fence, epoch, false)) return false;
  outputRate_.Reset();
  if (!MarkOutputLocked(false)) return false;
  return true;
}

bool EspIdfVoiceAudio::Play(const std::vector<uint8_t>& pcm) {
  return PlayPlayback(nullptr, pcm.data(), pcm.size());
}
bool EspIdfVoiceAudio::Play(const Fence& fence, const uint8_t* pcm, size_t length) {
  return PlayPlayback(&fence, pcm, length);
}
bool EspIdfVoiceAudio::PlayPlayback(const Fence* fence, const uint8_t* pcm, size_t length) {
  if (!available_ || !pcm || !length || length % 2 || length > 32768) return false;
  uint32_t epoch;
  bool first;
  {
    std::lock_guard<std::mutex> muteLock(muteGate_);
    if (!active_.load() || failed_.load() || !Allowed(fence)) return false;
    epoch = epoch_.load();
    first = !playing_.exchange(true);
    if (first && !MarkOutputLocked(true)) return false;
    if (!continuous_.load()) listening_.store(false);
  }
  if (first) outputRate_.Reset();
  PlaybackScratch scratch;
  for (size_t offset = 0; offset < length; offset += 1024) {
    if (!PlaybackCurrent(fence, epoch, true)) return false;
    scratch.samples.clear();
    const size_t end = std::min(length, offset + 1024);
    for (size_t i = offset; i < end; i += 2)
      outputRate_.Push(static_cast<int16_t>(pcm[i] | (pcm[i + 1] << 8)), scratch.samples);
    for (size_t i = 0; i < scratch.samples.size(); ++i) {
      scratch.slots[i * 2] = scratch.slots[i * 2 + 1] = static_cast<int32_t>(scratch.samples[i]) * 16384;
    }
    bool unmuted;
    {
      std::lock_guard<std::mutex> muteLock(muteGate_);
      if (!PlaybackCurrent(fence, epoch, true)) return false;
      unmuted = Mute(true);
    }
    if (!unmuted) return PlaybackFault(fence, epoch);
    std::lock_guard<std::mutex> outputLock(outputGate_);
    {
      std::lock_guard<std::mutex> muteLock(muteGate_);
      if (!PlaybackCurrent(fence, epoch, true)) return false;
    }
    size_t written = 0;
    const bool wrote = i2s_channel_write(tx_, scratch.slots.data(), scratch.samples.size() * 8, &written, 100) == ESP_OK &&
        written == scratch.samples.size() * 8;
    if (!wrote) return PlaybackFault(fence, epoch);
    std::lock_guard<std::mutex> muteLock(muteGate_);
    if (!PlaybackCurrent(fence, epoch, true)) return false;
  }
  return true;
}

bool EspIdfVoiceAudio::Drain() { return DrainPlayback(nullptr); }
bool EspIdfVoiceAudio::Drain(const Fence& fence) { return DrainPlayback(&fence); }
bool EspIdfVoiceAudio::DrainPlayback(const Fence* fence) {
  uint32_t epoch;
  {
    std::lock_guard<std::mutex> muteLock(muteGate_);
    epoch = epoch_.load();
    if (!PlaybackCurrent(fence, epoch, true)) return false;
  }
  std::array<int32_t, 640> silence{};
  for (int i = 0; i < 9; ++i) {
    std::lock_guard<std::mutex> outputLock(outputGate_);
    {
      std::lock_guard<std::mutex> muteLock(muteGate_);
      if (!PlaybackCurrent(fence, epoch, true)) return false;
    }
    size_t written = 0;
    const bool wrote = i2s_channel_write(tx_, silence.data(), sizeof(silence), &written, 100) == ESP_OK &&
        written == sizeof(silence);
    if (!wrote) return PlaybackFault(fence, epoch);
    std::lock_guard<std::mutex> muteLock(muteGate_);
    if (!PlaybackCurrent(fence, epoch, true)) return false;
  }
  std::lock_guard<std::mutex> muteLock(muteGate_);
  if (!PlaybackCurrent(fence, epoch, true)) return false;
  const bool muted = Mute(false);
  playing_.store(false);
  return MarkOutputLocked(false) && muted;
}

void EspIdfVoiceAudio::Cue(uint16_t frequencyHz, uint16_t durationMs) {
  (void)TryCue(frequencyHz, durationMs);
}

PresentationAudioPort::CueResult EspIdfVoiceAudio::TryCue(uint16_t frequencyHz, uint16_t durationMs) {
  // Optional sound never acquires a mutex on the control loop. Voice owns TX
  // whenever active; effects cannot interrupt listening or remote speech.
  if (!board_ || !available_ || active_.load() || failed_.load() || frequencyHz < 80 ||
      frequencyHz > 2000 || durationMs == 0 || durationMs > 200) return CueResult::Unavailable;
  if (cueGate_.test_and_set(std::memory_order_acquire)) return CueResult::Unavailable;
  if (active_.load() || failed_.load()) {
    cueGate_.clear(std::memory_order_release);
    return CueResult::Unavailable;
  }
  cue_.store((static_cast<uint32_t>(durationMs) << 16) | frequencyHz);
  cueGate_.clear(std::memory_order_release);
  return CueResult::Requested; // Enqueue evidence only; DMA/acoustics unknown.
}

void EspIdfVoiceAudio::StopCues() { cue_.store(0); }

void EspIdfVoiceAudio::PumpCue() {
  if (cueGate_.test_and_set(std::memory_order_acquire)) return;
  struct ReleaseCueGate {
    std::atomic_flag& gate;
    ~ReleaseCueGate() { gate.clear(std::memory_order_release); }
  } release{cueGate_};
  std::lock_guard<std::mutex> outputLock(outputGate_);
  uint32_t cue = cue_.load();
  if (!cue || active_.load() || failed_.load()) { cueSample_ = 0; return; }
  const uint16_t frequency = static_cast<uint16_t>(cue);
  const uint16_t remainingMs = static_cast<uint16_t>(cue >> 16);
  const unsigned frames = std::min<unsigned>(remainingMs, 20) * 32;
  for (unsigned i = 0; i < frames; ++i) {
    // Peak 1/16 full scale, sine with a short fade to avoid abrupt clicks.
    const double envelope = std::min(1.0, std::min((cueSample_ + i) / 160.0,
        (remainingMs * 32.0 - i) / 160.0));
    const int32_t sample = static_cast<int32_t>(134217727.0 * envelope *
        std::sin(6.283185307179586 * frequency * (cueSample_ + i) / 32000.0));
    cuePcm_[2*i] = cuePcm_[2*i+1] = sample;
  }
  if (active_.load() || cue_.load() != cue) return;
  size_t written = 0;
  // B leaves amplifier SD unchanged. Never reopen profile A's hardware mute
  // from this optional path. Cancel discards future blocks; an admitted <=20ms
  // block can still drain through DMA, as with the existing voice Stop path.
  {
    std::lock_guard<std::mutex> muteLock(muteGate_);
    if (!MarkOutputLocked(true)) return;
  }
  if (!board_ || !board_->AudioReady() ||
      i2s_channel_write(tx_, cuePcm_.data(), frames * 8, &written, 25) != ESP_OK ||
      written != frames * 8) { cue_.store(0); cueSample_ = 0; return; }
  cueSample_ += frames;
  const uint32_t next = remainingMs > 20 ? ((remainingMs - 20u) << 16) | frequency : 0;
  if (!cue_.compare_exchange_strong(cue, next) || !next) cueSample_ = 0;
}

void EspIdfVoiceAudio::CaptureTask(void* argument) { static_cast<EspIdfVoiceAudio*>(argument)->Capture(); }
void EspIdfVoiceAudio::Capture() {
  std::array<int32_t, 1280> slots{}; // 20 ms, stereo.
  PcmRateConverter rate(32000, 24000);
  std::vector<int16_t> samples;
  samples.reserve(480);
  uint32_t previousEpoch = UINT32_MAX;
  while (true) {
    size_t read = 0;
    const uint32_t epoch = captureEpoch_.load();
    const uint64_t taint = outputTaint_.load();
    const bool unsafe = outputUnsafe_.load();
    const esp_err_t result = i2s_channel_read(rx_, slots.data(), sizeof(slots), &read, 100);
    bool listening = false;
    {
      std::lock_guard<std::mutex> muteLock(muteGate_);
      if (epoch != captureEpoch_.load()) continue;
      listening = listening_.load() && active_.load();
      if (!listening) {
        if (input_) xQueueReset(input_);
      } else if (result != ESP_OK || read != sizeof(slots)) {
        // An old failed DMA borrow must never stop a newer capture lease.
        failed_.store(true); StopCues(); StopLocked();
        continue;
      }
    }
    if (!listening) { PumpCue(); continue; }
    if (epoch != previousEpoch) { rate.Reset(); previousEpoch = epoch; }
    const bool taintedBeforeConversion = unsafe || outputUnsafe_.load() || taint != outputTaint_.load();
    if (taintedBeforeConversion) {
      // Speaker samples must not survive in the RX FIR and leak into a later
      // quiet block even when this entire current frame will be silenced.
      rate.Reset(); slots.fill(0);
    }
    samples.clear();
    for (size_t i = 0; i < slots.size(); i += 2)
      rate.Push(static_cast<int16_t>(slots[i] >> 16), samples); // ICS43434 left slot.
    Frame frame{};
    frame.epoch = epoch;
    frame.bytes = static_cast<uint16_t>(samples.size() * 2);
    for (size_t i = 0; i < samples.size(); ++i) {
      frame.pcm[i * 2] = static_cast<uint8_t>(samples[i]);
      frame.pcm[i * 2 + 1] = static_cast<uint8_t>(static_cast<uint16_t>(samples[i]) >> 8);
    }
    // ESP hardware has no verified AEC: keep the Live timeline moving with
    // silence while its speaker plays. Phone GPT-Live uses voice processing.
    if (taintedBeforeConversion || outputUnsafe_.load() || taint != outputTaint_.load()) {
      rate.Reset();
      std::memset(frame.pcm, 0, frame.bytes);
      if (!continuous_.load()) {
        std::fill(samples.begin(), samples.end(), 0);
        continue; // Realtime input remains closed until TX tail is quiescent.
      }
    }
    {
      std::lock_guard<std::mutex> muteLock(muteGate_);
      if (epoch == captureEpoch_.load() && active_.load() && listening_.load() && !failed_.load() &&
          xQueueSend(input_, &frame, 0) != pdTRUE) {
        if (captureOverruns_.load() != UINT32_MAX) captureOverruns_.fetch_add(1);
        failed_.store(true); StopCues(); StopLocked();
      }
      const auto queued = CaptureQueuedBytes();
      if (queued > captureHighWater_.load()) captureHighWater_.store(queued);
    }
    std::memset(&frame, 0, sizeof(frame));
    std::fill(samples.begin(), samples.end(), 0);
  }
}

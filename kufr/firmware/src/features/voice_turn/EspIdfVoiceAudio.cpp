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

void EspIdfVoiceAudio::Begin() {
  Stop();
  std::lock_guard<std::mutex> outputLock(outputGate_);
  if (board_ && !board_->AudioReady()) { failed_.store(true); return; }
  failed_.store(false);
  if (input_) xQueueReset(input_);
  outputRate_.Reset();
  active_.store(true);
}

void EspIdfVoiceAudio::Listen(bool enabled) {
  listening_.store(false);
  if (!Mute(false)) { Stop(); return; }
  epoch_.fetch_add(1);
  if (input_) xQueueReset(input_);
  if (enabled) {
    failed_.store(false);
    playing_.store(false);
    outputRate_.Reset();
    muteGate_.lock();
    listening_.store(active_.load());
    muteGate_.unlock();
  }
}

void EspIdfVoiceAudio::Stop() {
  StopCues();
  muteGate_.lock();
  active_.store(false);
  listening_.store(false);
  playing_.store(false);
  epoch_.fetch_add(1);
  if (available_) Mute(false);
  muteGate_.unlock();
}

bool EspIdfVoiceAudio::TakeInput(std::vector<uint8_t>& pcm) {
  pcm.clear();
  Frame frame{};
  const uint32_t epoch = epoch_.load();
  for (int i = 0; i < 5; ++i) {
    if (!listening_.load() || failed_.load()) break;
    if (xQueueReceive(input_, &frame, pdMS_TO_TICKS(30)) != pdTRUE) break;
    if (frame.epoch == epoch && epoch_.load() == epoch)
      pcm.insert(pcm.end(), frame.pcm, frame.pcm + frame.bytes);
  }
  std::memset(&frame, 0, sizeof(frame));
  return !failed_.load();
}

bool EspIdfVoiceAudio::CancelPlayback() {
  // The network reader owns Play and this method. Stop remains sticky on the
  // independent control task. Mute first: a short admitted DMA tail can remain.
  const uint32_t epoch = epoch_.load();
  {
    std::lock_guard<std::mutex> muteLock(muteGate_);
    if (!active_.load() || failed_.load()) return false;
    playing_.store(false);
    if (!Mute(false)) return false;
  }
  // Replace the eight descriptor DMA tail with silence before a new Play can
  // open output. B's SD cannot mute instantly; the admitted tail is a physical
  // latency limit, but it must never be reopened by the next response.
  std::array<int32_t, 640> silence{};
  for (int i = 0; i < 9; ++i) {
    std::lock_guard<std::mutex> outputLock(outputGate_);
    size_t written = 0;
    if (!active_.load() || epoch_.load() != epoch ||
        i2s_channel_write(tx_, silence.data(), sizeof(silence), &written, 100) != ESP_OK ||
        written != sizeof(silence)) return false;
  }
  outputRate_.Reset();
  return true;
}

bool EspIdfVoiceAudio::Play(const std::vector<uint8_t>& pcm) {
  if (!available_ || pcm.empty() || pcm.size() % 2 || pcm.size() > 32768) return false;
  // Stop is sticky until Begin. A block admitted just before cancellation must
  // not reopen the speaker after the control task has already muted it.
  muteGate_.lock();
  const bool active = active_.load();
  const uint32_t epoch = epoch_.load();
  const bool first = active && !playing_.exchange(true);
  if (!continuous_.load()) listening_.store(false);
  muteGate_.unlock();
  if (!active) return false;
  if (first) outputRate_.Reset();
  std::vector<int16_t> samples;
  samples.reserve(684);
  std::array<int32_t, 1368> slots{};
  for (size_t offset = 0; offset < pcm.size(); offset += 1024) {
    if (!playing_.load() || epoch_.load() != epoch) return false;
    samples.clear();
    const size_t end = std::min(pcm.size(), offset + 1024);
    for (size_t i = offset; i < end; i += 2)
      outputRate_.Push(static_cast<int16_t>(pcm[i] | (pcm[i + 1] << 8)), samples);
    for (size_t i = 0; i < samples.size(); ++i) {
      // Conservative initial volume 25%; duplicate into stereo 32-bit slots.
      slots[i * 2] = slots[i * 2 + 1] = static_cast<int32_t>(samples[i]) * 16384;
    }
    muteGate_.lock();
    const bool allowed = playing_.load() && epoch_.load() == epoch;
    const bool unmuted = allowed && Mute(true);
    muteGate_.unlock();
    if (!unmuted) { Stop(); return false; }
    std::lock_guard<std::mutex> outputLock(outputGate_);
    size_t written = 0;
    if (i2s_channel_write(tx_, slots.data(), samples.size() * 8, &written, 100) != ESP_OK ||
        written != samples.size() * 8) { Stop(); return false; }
    if (!playing_.load() || epoch_.load() != epoch) { Mute(false); return false; }
  }
  std::fill(samples.begin(), samples.end(), 0);
  return true;
}

bool EspIdfVoiceAudio::Drain() {
  // Flush the queued DMA with silence before ACK. Stop remains immediate on its
  // own task; no playback-complete ACK is issued if interrupted.
  const uint32_t epoch = epoch_.load();
  std::array<int32_t, 640> silence{};
  for (int i = 0; i < 9; ++i) {
    std::lock_guard<std::mutex> outputLock(outputGate_);
    size_t written = 0;
    if (!playing_.load() || epoch_.load() != epoch ||
        i2s_channel_write(tx_, silence.data(), sizeof(silence), &written, 100) != ESP_OK ||
        written != sizeof(silence)) return false;
  }
  const bool muted = Mute(false);
  playing_.store(false);
  return muted;
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
    const uint32_t epoch = epoch_.load();
    const esp_err_t result = i2s_channel_read(rx_, slots.data(), sizeof(slots), &read, 100);
    if (!listening_.load()) {
      PumpCue();
      Frame discarded{};
      for (unsigned i = 0; i < 20 && xQueueReceive(input_, &discarded, 0) == pdTRUE; ++i)
        std::memset(&discarded, 0, sizeof(discarded));
      continue; // Drain RX also during playback; never upload speaker echo.
    }
    if (result != ESP_OK || read != sizeof(slots)) { failed_.store(true); Stop(); continue; }
    if (epoch != epoch_.load()) continue;
    if (epoch != previousEpoch) { rate.Reset(); previousEpoch = epoch; }
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
    if (continuous_.load() && playing_.load()) std::memset(frame.pcm, 0, frame.bytes);
    if (xQueueSend(input_, &frame, 0) != pdTRUE) { failed_.store(true); Stop(); }
    std::memset(&frame, 0, sizeof(frame));
    std::fill(samples.begin(), samples.end(), 0);
  }
}

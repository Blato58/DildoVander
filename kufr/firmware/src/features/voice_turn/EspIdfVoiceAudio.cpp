#include "EspIdfVoiceAudio.h"
#include <array>
#include <cstring>
#include "driver/gpio.h"
#include "nvs.h"

namespace {
// Explicitly provisioned profile A, camera/SD absent. Never enable guessed GPIO
// wiring on an unverified board. Profile B needs a separate expander adapter.
constexpr gpio_num_t Mute = GPIO_NUM_16;
}

bool EspIdfVoiceAudio::Initialize(bool nvsInitialized) {
  nvs_handle_t handle = 0;
  uint8_t verified = 0;
  if (!nvsInitialized || nvs_open("wand_audio", NVS_READONLY, &handle) != ESP_OK) return false;
  const bool wired = nvs_get_u8(handle, "profile_a_ok", &verified) == ESP_OK && verified == 1;
  nvs_close(handle);
  if (!wired) return false;
  gpio_set_direction(Mute, GPIO_MODE_OUTPUT);
  gpio_set_level(Mute, 0);
  i2s_chan_config_t channels = I2S_CHANNEL_DEFAULT_CONFIG(I2S_NUM_AUTO, I2S_ROLE_MASTER);
  channels.dma_desc_num = 8;
  channels.dma_frame_num = 320;
  channels.auto_clear = true;
  if (i2s_new_channel(&channels, &tx_, &rx_) != ESP_OK) return false;
  i2s_std_config_t standard = {};
  standard.clk_cfg = I2S_STD_CLK_DEFAULT_CONFIG(32000);
  standard.slot_cfg = I2S_STD_PHILIPS_SLOT_DEFAULT_CONFIG(I2S_DATA_BIT_WIDTH_32BIT, I2S_SLOT_MODE_STEREO);
  standard.gpio_cfg.mclk = I2S_GPIO_UNUSED;
  standard.gpio_cfg.bclk = GPIO_NUM_4;
  standard.gpio_cfg.ws = GPIO_NUM_5;
  standard.gpio_cfg.dout = GPIO_NUM_7;
  standard.gpio_cfg.din = GPIO_NUM_6;
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
  failed_.store(false);
  if (input_) xQueueReset(input_);
  outputRate_.Reset();
}

void EspIdfVoiceAudio::Listen(bool enabled) {
  listening_.store(false);
  gpio_set_level(Mute, 0);
  epoch_.fetch_add(1);
  if (input_) xQueueReset(input_);
  if (enabled) {
    failed_.store(false);
    playing_.store(false);
    outputRate_.Reset();
    listening_.store(true);
  }
}

void EspIdfVoiceAudio::Stop() {
  portENTER_CRITICAL(&muteGate_);
  listening_.store(false);
  playing_.store(false);
  epoch_.fetch_add(1);
  if (available_) gpio_set_level(Mute, 0);
  portEXIT_CRITICAL(&muteGate_);
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

bool EspIdfVoiceAudio::Play(const std::vector<uint8_t>& pcm) {
  if (!available_ || pcm.empty() || pcm.size() % 2 || pcm.size() > 32768) return false;
  if (!playing_.exchange(true)) outputRate_.Reset();
  listening_.store(false);
  std::vector<int16_t> samples;
  samples.reserve(684);
  std::array<int32_t, 1368> slots{};
  const uint32_t epoch = epoch_.load();
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
    portENTER_CRITICAL(&muteGate_);
    const bool allowed = playing_.load() && epoch_.load() == epoch;
    if (allowed) gpio_set_level(Mute, 1);
    portEXIT_CRITICAL(&muteGate_);
    if (!allowed) return false;
    size_t written = 0;
    if (i2s_channel_write(tx_, slots.data(), samples.size() * 8, &written, 100) != ESP_OK ||
        written != samples.size() * 8) { Stop(); return false; }
    if (!playing_.load() || epoch_.load() != epoch) { gpio_set_level(Mute, 0); return false; }
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
    size_t written = 0;
    if (!playing_.load() || epoch_.load() != epoch ||
        i2s_channel_write(tx_, silence.data(), sizeof(silence), &written, 100) != ESP_OK ||
        written != sizeof(silence)) return false;
  }
  gpio_set_level(Mute, 0);
  playing_.store(false);
  return true;
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
    if (xQueueSend(input_, &frame, 0) != pdTRUE) { failed_.store(true); Stop(); }
    std::memset(&frame, 0, sizeof(frame));
    std::fill(samples.begin(), samples.end(), 0);
  }
}

#include "EspIdfPhysicalIo.h"

#include <cstring>
#include "driver/gpio.h"
#include "esp_rom_sys.h"
#include "esp_timer.h"
#include "nvs.h"

namespace {
constexpr gpio_num_t RunPin = static_cast<gpio_num_t>(KufrBoard::ProfileB.run);
// This effects profile deliberately leaves SD unmounted. GPIO38 is GOOD input.
constexpr gpio_num_t GoodPin = static_cast<gpio_num_t>(KufrBoard::WatchdogGood);
constexpr int IoTimeoutMs = 10;
bool Flag(nvs_handle_t handle, const char* key) {
  uint8_t value = 0;
  return nvs_get_u8(handle, key, &value) == ESP_OK && value == 1;
}
}

bool EspIdfPhysicalIo::Fail() {
  failed_.store(true);
  if (profileVerified_) gpio_set_level(RunPin, 0);
  return false;
}

void EspIdfPhysicalIo::RunRequest(bool enabled) {
  if (profileVerified_) gpio_set_level(RunPin, enabled && Available() && inhibitVerified_);
}

bool EspIdfPhysicalIo::Write(i2c_master_dev_handle_t device, uint8_t reg,
                            const uint8_t* bytes, size_t size) {
  if (!device || size > 65) return Fail();
  uint8_t data[66]{};
  data[0] = reg;
  std::memcpy(data + 1, bytes, size);
  return i2c_master_transmit(device, data, size + 1, IoTimeoutMs) == ESP_OK || Fail();
}

bool EspIdfPhysicalIo::Read(i2c_master_dev_handle_t device, uint8_t reg,
                           uint8_t* bytes, size_t size) {
  return device && (i2c_master_transmit_receive(device, &reg, 1, bytes, size, IoTimeoutMs) == ESP_OK || Fail());
}

bool EspIdfPhysicalIo::Initialize(bool nvsReady) {
  nvs_handle_t handle;
  if (!nvsReady || nvs_open("wand_hw", NVS_READONLY, &handle) != ESP_OK) return false;
  profileSelected_ = Flag(handle, "profile_b_ok");
  profileVerified_ = profileSelected_ && Flag(handle, "mute_ok");
  inhibitVerified_ = Flag(handle, "inhibit_ok") && Flag(handle, "positions_ok");
  nvs_close(handle);
  if (!profileVerified_) return false;
  gpio_set_level(RunPin, 0);
  if (gpio_set_direction(RunPin, GPIO_MODE_OUTPUT) != ESP_OK ||
      gpio_set_direction(GoodPin, GPIO_MODE_INPUT) != ESP_OK) return Fail();
  mutex_ = xSemaphoreCreateMutex();
  if (!mutex_) return Fail();
  i2c_master_bus_config_t busConfig{};
  busConfig.i2c_port = I2C_NUM_0;
  busConfig.sda_io_num = GPIO_NUM_1;
  busConfig.scl_io_num = GPIO_NUM_14;
  busConfig.clk_source = I2C_CLK_SRC_DEFAULT;
  busConfig.glitch_ignore_cnt = 7;
  if (i2c_new_master_bus(&busConfig, &bus_) != ESP_OK) return Fail();
  i2c_device_config_t device{};
  device.dev_addr_length = I2C_ADDR_BIT_LEN_7;
  device.scl_speed_hz = 100000;
  device.device_address = KufrBoard::PcaAddress;
  if (i2c_master_bus_add_device(bus_, &device, &pca_) != ESP_OK) return Fail();
  device.device_address = KufrBoard::McpAddress;
  if (i2c_master_bus_add_device(bus_, &device, &mcp_) != ESP_OK) return Fail();
  // BANK=0 is the reset state; no other owner may reconfigure this expander.
  const uint8_t zero = 0, directions[] = {0xf9, 0xff}, pulls[] = {0xf1, 0xff};
  if (!Write(mcp_, 0x0a, &zero, 1) || !Write(mcp_, 0x14, &zero, 1) ||
      !Write(mcp_, 0x0c, pulls, 2) || !Write(mcp_, 0x00, directions, 2)) return Fail();
  // Sleep while changing prescaler; 25MHz / (4096 * 122) ~= 50Hz.
  const uint8_t sleep = 0x30, mode2 = 0x04, prescale = 121, wake = 0x20;
  if (!Write(pca_, 0x00, &sleep, 1) || !Write(pca_, 0x01, &mode2, 1) ||
      !Write(pca_, 0xfe, &prescale, 1) || !FullOff() || !Write(pca_, 0x00, &wake, 1)) return Fail();
  esp_rom_delay_us(500);  // Oscillator startup, once, while inhibited.
  rmt_tx_channel_config_t rgbConfig{};
  rgbConfig.gpio_num = GPIO_NUM_48;
  rgbConfig.clk_src = RMT_CLK_SRC_DEFAULT;
  rgbConfig.resolution_hz = 10000000;
  rgbConfig.mem_block_symbols = 64;
  rgbConfig.trans_queue_depth = 1;
  rmt_copy_encoder_config_t copy{};
  if (rmt_new_tx_channel(&rgbConfig, &rgb_) != ESP_OK ||
      rmt_new_copy_encoder(&copy, &encoder_) != ESP_OK || rmt_enable(rgb_) != ESP_OK) return Fail();
  available_ = true;
  std::array<uint8_t, 108> dark{};
  if (!Lights(dark) || xTaskCreate(PulseTask, "hw_heartbeat", 3072, this, 6, &pulseTask_) != pdPASS)
    return Fail();
  return true;
}

bool EspIdfPhysicalIo::LoadCalibration(std::array<ServoCalibration, 6>& calibration,
                                      uint64_t& revision) {
  nvs_handle_t handle;
  if (!Available() || nvs_open("wand_hw", NVS_READONLY, &handle) != ESP_OK) return false;
  uint8_t bytes[36]{};
  size_t size = sizeof(bytes);
  const bool valid = nvs_get_u64(handle, "cal_revision", &revision) == ESP_OK &&
                     nvs_get_blob(handle, "servos", bytes, &size) == ESP_OK && size == sizeof(bytes);
  nvs_close(handle);
  if (!valid) return false;
  for (unsigned i = 0; i < 6; ++i) {
    const auto* p = bytes + i * 6;
    calibration[i] = {static_cast<uint16_t>(p[0] | p[1] << 8),
                      static_cast<uint16_t>(p[2] | p[3] << 8),
                      static_cast<uint16_t>(p[4] | p[5] << 8)};
  }
  return true;
}

bool EspIdfPhysicalIo::FullOff() {
  if (!pca_) return !failed_.load();
  // Clear per-channel FULL_ON as well as ALL_LED override. Future writes must
  // not resurrect a latched motor. RUN_REQUEST is already LOW on every caller.
  if (xSemaphoreTake(mutex_, pdMS_TO_TICKS(20)) != pdTRUE) return Fail();
  uint8_t channels[64]{};
  for (unsigned i = 0; i < 16; ++i) channels[i * 4 + 3] = 0x10;
  const uint8_t all[] = {0, 0, 0, 0};
  const bool result = Write(pca_, 0x06, channels, sizeof(channels)) && Write(pca_, 0xfa, all, 4);
  xSemaphoreGive(mutex_);
  return result;
}

bool EspIdfPhysicalIo::Servo(uint8_t channel, uint16_t pulseUs) {
  if (!Available() || channel >= 6 || pulseUs < 500 || pulseUs > 2500) return false;
  const uint16_t ticks = static_cast<uint16_t>((static_cast<uint32_t>(pulseUs) * 25 + 61) / 122);
  const uint8_t bytes[] = {0, 0, static_cast<uint8_t>(ticks), static_cast<uint8_t>(ticks >> 8)};
  if (xSemaphoreTake(mutex_, pdMS_TO_TICKS(20)) != pdTRUE) return Fail();
  const bool result = Write(pca_, static_cast<uint8_t>(0x06 + channel * 4), bytes, 4);
  xSemaphoreGive(mutex_);
  return result;
}

bool EspIdfPhysicalIo::Vibration(uint8_t channel, bool enabled) {
  if (!Available() || channel < 6 || channel > 11) return false;
  const uint8_t bytes[] = {0, static_cast<uint8_t>(enabled ? 0x10 : 0), 0,
                         static_cast<uint8_t>(enabled ? 0 : 0x10)};
  if (xSemaphoreTake(mutex_, pdMS_TO_TICKS(20)) != pdTRUE) return Fail();
  const bool result = Write(pca_, static_cast<uint8_t>(0x06 + channel * 4), bytes, 4);
  xSemaphoreGive(mutex_);
  return result;
}

bool EspIdfPhysicalIo::Lights(const std::array<uint8_t, 108>& grb) {
  if (!Available()) return false;
  size_t index = 0;
  for (auto byte : grb) for (int bit = 7; bit >= 0; --bit) {
    auto& symbol = symbols_[index++];
    const bool one = (byte & (1u << bit)) != 0;
    symbol.level0 = 1; symbol.duration0 = one ? 8 : 3;
    symbol.level1 = 0; symbol.duration1 = one ? 4 : 9;
  }
  auto& reset = symbols_.back();
  reset.level0 = 0; reset.duration0 = 500;
  reset.level1 = 0; reset.duration1 = 500;
  rmt_transmit_config_t tx{};
  return (rmt_transmit(rgb_, encoder_, symbols_.data(), sizeof(symbols_), &tx) == ESP_OK &&
          rmt_tx_wait_all_done(rgb_, 10) == ESP_OK) || Fail();
}

bool EspIdfPhysicalIo::McpOutput(uint8_t pin, bool enabled) {
  if (!Available() || (pin != 1 && pin != 2)) return false;
  if (xSemaphoreTake(mutex_, pdMS_TO_TICKS(20)) != pdTRUE) return Fail();
  const uint8_t next = enabled ? output_ | (1u << pin) : output_ & ~(1u << pin);
  const bool result = Write(mcp_, 0x14, &next, 1);
  if (result) output_ = next;
  xSemaphoreGive(mutex_);
  return result;
}
bool EspIdfPhysicalIo::SetAudioEnabled(bool enabled) { return McpOutput(1, enabled); }

bool EspIdfPhysicalIo::ReadInputs(PhysicalInputs& inputs) {
  if (!Available()) return false;
  uint8_t gpio[2]{};
  if (xSemaphoreTake(mutex_, pdMS_TO_TICKS(20)) != pdTRUE) return Fail();
  const bool result = Read(mcp_, 0x12, gpio, 2);
  xSemaphoreGive(mutex_);
  if (!result) return false;
  const uint16_t levels = static_cast<uint16_t>(gpio[0] | gpio[1] << 8);
  inputs.enabled = (levels & 8) != 0;
  inputs.button = (levels & 1) == 0;
  inputs.watchdogGood = gpio_get_level(GoodPin) != 0;
  inputs.raisedMask = static_cast<uint8_t>((~levels >> 4) & 63);
  inputs.restingMask = static_cast<uint8_t>((~levels >> 10) & 63);
  return true;
}

void EspIdfPhysicalIo::Heartbeat(uint64_t nowMs) {
  if (!Available() || !inhibitVerified_ || !pulseTask_) return;
  requestedAt_.store(nowMs);
  xTaskNotifyGive(pulseTask_);  // One collapsed request, never a replay queue.
}
void EspIdfPhysicalIo::PulseTask(void* context) {
  auto& self = *static_cast<EspIdfPhysicalIo*>(context);
  while (true) {
    ulTaskNotifyTake(pdTRUE, portMAX_DELAY);
    const uint64_t nowMs = static_cast<uint64_t>(esp_timer_get_time() / 1000);
    if (nowMs < self.requestedAt_.load() || nowMs - self.requestedAt_.load() > 20) {
      self.Fail(); continue;
    }
    if (!self.Available()) continue;
    if (xSemaphoreTake(self.mutex_, pdMS_TO_TICKS(20)) != pdTRUE) { self.Fail(); continue; }
    // Hold the shared bus for the complete single pulse. A 64-byte PCA clear
    // or audio mute write must not stretch its HIGH interval past 2.5ms.
    const uint64_t admittedMs = static_cast<uint64_t>(esp_timer_get_time() / 1000);
    if (admittedMs < self.requestedAt_.load() || admittedMs - self.requestedAt_.load() > 20) {
      xSemaphoreGive(self.mutex_); self.Fail(); continue;
    }
    uint8_t next = self.output_ | 4;
    const bool high = self.Write(self.mcp_, 0x14, &next, 1);
    const int64_t started = esp_timer_get_time();
    if (high) esp_rom_delay_us(1000);
    next = self.output_ & ~4;
    const bool low = self.Write(self.mcp_, 0x14, &next, 1);
    const bool timely = esp_timer_get_time() - started <= 2500;
    xSemaphoreGive(self.mutex_);
    if (!high || !low || !timely) self.Fail();
  }
}

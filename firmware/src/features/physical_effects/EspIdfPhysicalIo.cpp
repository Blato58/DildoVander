#include "EspIdfPhysicalIo.h"

#include <cstring>
#include <algorithm>
#include "esp_timer.h"
#include "driver/gpio.h"
#include "esp_rom_sys.h"
#include "nvs.h"

namespace {
constexpr gpio_num_t OePin = static_cast<gpio_num_t>(KufrBoard::ProfileB.oe);


}

bool EspIdfPhysicalIo::Fail() {
  failed_.store(true);
  RequestUrgentStop();
  return false;
}

void EspIdfPhysicalIo::EnableOutputs(bool enabled) {
  // Only GPIO plus atomic loads share this critical section. No bus operation,
  // allocator, worker wait or transfer timeout can delay an urgent OE request.
  portENTER_CRITICAL(&outputMux_);
  if (profileVerified_) gpio_set_level(OePin, enabled && Available() && !UrgentStopPending() ? 0 : 1);
  portEXIT_CRITICAL(&outputMux_);
}

void EspIdfPhysicalIo::RequestUrgentStop() {
  portENTER_CRITICAL(&outputMux_);
  stopEpoch_.fetch_add(2);
  stopEpoch_.fetch_or(1u);
  if (profileVerified_) gpio_set_level(OePin, 1);
  portEXIT_CRITICAL(&outputMux_);
}

bool EspIdfPhysicalIo::ClearUrgentStopAfterInspection(uint32_t expectedEpoch) {
  if (!Available()) return false;
  return stopEpoch_.compare_exchange_strong(expectedEpoch, expectedEpoch & ~1u);
}

bool EspIdfPhysicalIo::Write(i2c_master_dev_handle_t device, uint8_t reg,
                            const uint8_t* bytes, size_t size) {
  if (!device || size > 65) return Fail();
  uint8_t data[66]{};
  data[0] = reg;
  std::memcpy(data + 1, bytes, size);
  const int timeoutMs = TransferTimeout();
  if (timeoutMs == 0) return Fail();
  const auto startedAtUs = static_cast<uint64_t>(esp_timer_get_time());
  const bool result = i2c_master_transmit(device, data, size + 1, timeoutMs) == ESP_OK;
  RecordTransfer(startedAtUs);
  return result || Fail();
}

bool EspIdfPhysicalIo::Read(i2c_master_dev_handle_t device, uint8_t reg,
                           uint8_t* bytes, size_t size) {
  const int timeoutMs = TransferTimeout();
  if (!device || timeoutMs == 0) return Fail();
  const auto startedAtUs = static_cast<uint64_t>(esp_timer_get_time());
  const bool result = i2c_master_transmit_receive(device, &reg, 1, bytes, size, timeoutMs) == ESP_OK;
  RecordTransfer(startedAtUs);
  return result || Fail();
}

bool EspIdfPhysicalIo::Initialize(bool nvsReady) {
  nvs_handle_t handle;
  if (!nvsReady || nvs_open("wand_hw", NVS_READONLY, &handle) != ESP_OK) return false;
  uint8_t verified = 0;
  profileSelected_ = nvs_get_u8(handle, "profile_b_ok", &verified) == ESP_OK;
  profileVerified_ = profileSelected_ && verified == 1;
  nvs_close(handle);
  if (!profileVerified_) return false;
  gpio_set_level(OePin, 1);
  if (gpio_set_direction(OePin, GPIO_MODE_OUTPUT) != ESP_OK) return Fail();
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
  const uint8_t zero = 0, directions[] = {0xff, 0xff}, pulls[] = {0x01, 0x00};
  if (!Write(mcp_, 0x0a, &zero, 1) || !Write(mcp_, 0x14, &zero, 1) ||
      !Write(mcp_, 0x0c, pulls, 2) || !Write(mcp_, 0x00, directions, 2)) return Fail();
  // Sleep while changing prescaler; 25MHz / (4096 * 122) ~= 50Hz.
  const uint8_t sleep = 0x30, mode2 = 0x04, prescale = 121, wake = 0x20;
  if (!Write(pca_, 0x00, &sleep, 1) || !Write(pca_, 0x01, &mode2, 1) ||
      !Write(pca_, 0xfe, &prescale, 1) || !FullOff() || !Write(pca_, 0x00, &wake, 1)) return Fail();
  esp_rom_delay_us(500);  // Oscillator startup, once, with OE disabled.
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
  if (!Lights(dark)) return Fail();
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
  // GPIO shutdown precedes every attempt to obtain the shared bus.
  EnableOutputs(false);
  if (!pca_) return !failed_.load();
  // Clear per-channel FULL_ON as well as ALL_LED override. Future writes must
  // not resurrect a latched motor. OE is already HIGH on every caller.
  if (!TakeBus()) return Fail();
  bool result = true;
  if (!channelsReset_) {
    uint8_t channels[64]{};
    for (unsigned i = 0; i < 16; ++i) channels[i * 4 + 3] = 0x10;
    const uint8_t all[] = {0, 0, 0, 0};
    result = Write(pca_, 0x06, channels, sizeof(channels)) && Write(pca_, 0xfa, all, 4);
    channelsReset_ = result;
  } else {
    // This adapter is the only PCA owner. After the boot reset only a channel
    // explicitly written by Servo/Vibration can differ from FULL_OFF. Clear
    // those channels, including FULL_ON, instead of rewriting all 64 bytes on
    // every input/Stop pass. The domain permits one active actuator at a time.
    const uint8_t off[] = {0, 0, 0, 0x10};
    for (uint8_t channel = 0; channel < 16 && result; ++channel) {
      if (dirtyChannels_ & (1u << channel))
        result = Write(pca_, static_cast<uint8_t>(0x06 + channel * 4), off, sizeof(off));
    }
  }
  if (result) dirtyChannels_ = 0;
  xSemaphoreGive(mutex_);
  return result;
}

bool EspIdfPhysicalIo::Servo(uint8_t channel, uint16_t pulseUs) {
  if (!Available() || channel >= 6 || pulseUs < 500 || pulseUs > 2500) return false;
  const uint16_t ticks = static_cast<uint16_t>((static_cast<uint32_t>(pulseUs) * 25 + 61) / 122);
  const uint8_t bytes[] = {0, 0, static_cast<uint8_t>(ticks), static_cast<uint8_t>(ticks >> 8)};
  if (!TakeBus()) return Fail();
  dirtyChannels_ |= static_cast<uint16_t>(1u << channel);
  const bool result = Write(pca_, static_cast<uint8_t>(0x06 + channel * 4), bytes, 4);
  xSemaphoreGive(mutex_);
  return result;
}

bool EspIdfPhysicalIo::Vibration(uint8_t channel, bool enabled) {
  if (!Available() || channel < 6 || channel > 11) return false;
  const uint8_t bytes[] = {0, static_cast<uint8_t>(enabled ? 0x10 : 0), 0,
                         static_cast<uint8_t>(enabled ? 0 : 0x10)};
  if (!TakeBus()) return Fail();
  dirtyChannels_ |= static_cast<uint16_t>(1u << channel);
  const bool result = Write(pca_, static_cast<uint8_t>(0x06 + channel * 4), bytes, 4);
  xSemaphoreGive(mutex_);
  return result;
}

bool EspIdfPhysicalIo::Lights(const std::array<uint8_t, 108>& grb) {
  if (!Available()) return false;
  if (lightPending_) ++timing_.coalescedLedFrames;
  pendingGrb_ = grb;
  lightPending_ = true;
  return FlushLights();
}

bool EspIdfPhysicalIo::FlushLights() {
  if (!lightPending_) return true;
  if (lightInFlight_) {
    const auto result = rmt_tx_wait_all_done(rgb_, 0);
    if (result == ESP_ERR_TIMEOUT) return true;
    if (result != ESP_OK) return Fail();
    lightInFlight_ = false;
  }
  // The retained symbols belong to RMT until completion. Multiple same-pass
  // requests coalesce in pendingGrb_, never overwrite a transmitting frame.
  if (lightSubmitted_ && pendingGrb_ == submittedGrb_) { lightPending_ = false; return true; }
  const auto& grb = pendingGrb_;
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
  tx.flags.queue_nonblocking = 1;
  if (rmt_transmit(rgb_, encoder_, symbols_.data(), sizeof(symbols_), &tx) != ESP_OK) return Fail();
  submittedGrb_ = pendingGrb_;
  lightPending_ = false;
  lightInFlight_ = true;
  lightSubmitted_ = true;
  return true;
}

bool EspIdfPhysicalIo::ReadInputs(PhysicalInputs& inputs) {
  if (!Available()) return false;
  uint8_t gpio[2]{};
  if (!TakeBus()) return Fail();
  const bool result = Read(mcp_, 0x12, gpio, 2);
  xSemaphoreGive(mutex_);
  if (!result) return false;
  const uint16_t levels = static_cast<uint16_t>(gpio[0] | gpio[1] << 8);
  inputs.button = (levels & 1) == 0;
  return true;
}

void EspIdfPhysicalIo::BeginControlPass(uint64_t nowUs) {
  budget_.Begin(nowUs);
  passBusUs_ = 0;
  passOverBudget_ = false;
  if (Available() && !FlushLights()) Fail();
}

bool EspIdfPhysicalIo::TakeBus() {
  if (mutex_ && xSemaphoreTake(mutex_, 0) == pdTRUE) return true;
  ++timing_.lockRefusals;
  return false;
}

int EspIdfPhysicalIo::TransferTimeout() {
  const int remaining = budget_.RemainingMs(static_cast<uint64_t>(esp_timer_get_time()));
  if (remaining == 0) ++timing_.budgetRefusals;
  return remaining;
}

void EspIdfPhysicalIo::RecordTransfer(uint64_t startedAtUs) {
  const auto duration = static_cast<uint32_t>(static_cast<uint64_t>(esp_timer_get_time()) - startedAtUs);
  if (!budget_.Active()) return;
  passBusUs_ += duration;
  if (passBusUs_ > kufr::runtime::PhysicalIoBudget::ControlBudgetUs && !passOverBudget_) {
    ++timing_.overBudgetPasses;
    passOverBudget_ = true;
  }
  timing_.maximumTransferUs = std::max(timing_.maximumTransferUs, duration);
  timing_.maximumPassBusUs = std::max(timing_.maximumPassBusUs, passBusUs_);
}

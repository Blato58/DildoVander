#pragma once
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <vector>
using esp_err_t = int;
constexpr int ESP_OK = 0, ESP_FAIL = -1, ESP_ERR_TIMEOUT = 1;
using gpio_num_t = int;
constexpr int GPIO_NUM_1 = 1, GPIO_NUM_14 = 14, GPIO_NUM_48 = 48, GPIO_MODE_OUTPUT = 1;
using TickType_t = unsigned;
using SemaphoreHandle_t = void*;
using portMUX_TYPE = int;
#define portMUX_INITIALIZER_UNLOCKED 0
#define portENTER_CRITICAL(value) ((void)(value))
#define portEXIT_CRITICAL(value) ((void)(value))
#define pdMS_TO_TICKS(value) (value)
constexpr int pdTRUE = 1;
using i2c_master_bus_handle_t = void*;
using i2c_master_dev_handle_t = void*;
constexpr int I2C_NUM_0 = 0, I2C_CLK_SRC_DEFAULT = 0, I2C_ADDR_BIT_LEN_7 = 7;
struct i2c_master_bus_config_t { int i2c_port, sda_io_num, scl_io_num, clk_source, glitch_ignore_cnt; };
struct i2c_device_config_t { int dev_addr_length, scl_speed_hz, device_address; };
using rmt_channel_handle_t = void*;
using rmt_encoder_handle_t = void*;
constexpr int RMT_CLK_SRC_DEFAULT = 0;
struct rmt_symbol_word_t { unsigned level0, duration0, level1, duration1; };
struct rmt_tx_channel_config_t { int gpio_num, clk_src, resolution_hz, mem_block_symbols, trans_queue_depth; };
struct rmt_copy_encoder_config_t {};
struct rmt_transmit_config_t { struct { unsigned queue_nonblocking = 0; } flags; };
using nvs_handle_t = unsigned;
constexpr int NVS_READONLY = 0;
namespace io_stub {
struct Write { std::vector<uint8_t> bytes; int timeout; };
struct State {
  uint64_t nowUs = 0;
  uint32_t transferUs = 100;
  bool lockAvailable = true, rmtBusy = false, failTransfer = false;
  int oe = 1;
  unsigned lockCalls = 0, maximumLockWait = 0, reads = 0, transmits = 0, maximumRmtWait = 0;
  const void* retainedSymbols = nullptr;
  size_t retainedBytes = 0;
  bool nonblocking = false;
  std::vector<Write> writes;
};
extern State state;
}
inline int64_t esp_timer_get_time() { return static_cast<int64_t>(io_stub::state.nowUs); }
inline int gpio_set_level(gpio_num_t, int level) { io_stub::state.oe = level; return ESP_OK; }
inline int gpio_set_direction(gpio_num_t, int) { return ESP_OK; }
inline void esp_rom_delay_us(unsigned) {}
inline SemaphoreHandle_t xSemaphoreCreateMutex() { return reinterpret_cast<void*>(1); }
inline int xSemaphoreTake(SemaphoreHandle_t, unsigned ticks) {
  auto& s = io_stub::state; ++s.lockCalls;
  if (ticks > s.maximumLockWait) s.maximumLockWait = ticks;
  return s.lockAvailable ? pdTRUE : 0;
}
inline void xSemaphoreGive(SemaphoreHandle_t) {}
inline int i2c_new_master_bus(const i2c_master_bus_config_t*, i2c_master_bus_handle_t* bus) { *bus = reinterpret_cast<void*>(1); return ESP_OK; }
inline int i2c_master_bus_add_device(i2c_master_bus_handle_t, const i2c_device_config_t* config, i2c_master_dev_handle_t* device) {
  *device = reinterpret_cast<void*>(static_cast<uintptr_t>(config->device_address)); return ESP_OK;
}
inline int i2c_master_transmit(i2c_master_dev_handle_t, const uint8_t* bytes, size_t count, int timeout) {
  auto& s = io_stub::state; s.writes.push_back({std::vector<uint8_t>(bytes, bytes + count), timeout});
  s.nowUs += s.transferUs;
  return s.failTransfer ? ESP_ERR_TIMEOUT : ESP_OK;
}
inline int i2c_master_transmit_receive(i2c_master_dev_handle_t, const uint8_t*, size_t, uint8_t* bytes, size_t count, int timeout) {
  auto& s = io_stub::state; ++s.reads; s.nowUs += s.transferUs;
  if (timeout > 2) return ESP_FAIL;
  std::memset(bytes, 0xff, count); return s.failTransfer ? ESP_ERR_TIMEOUT : ESP_OK;
}
inline int rmt_new_tx_channel(const rmt_tx_channel_config_t*, rmt_channel_handle_t* value) { *value = reinterpret_cast<void*>(1); return ESP_OK; }
inline int rmt_new_copy_encoder(const rmt_copy_encoder_config_t*, rmt_encoder_handle_t* value) { *value = reinterpret_cast<void*>(1); return ESP_OK; }
inline int rmt_enable(rmt_channel_handle_t) { return ESP_OK; }
inline int rmt_transmit(rmt_channel_handle_t, rmt_encoder_handle_t, const void* bytes, size_t count, const rmt_transmit_config_t* config) {
  auto& s = io_stub::state; ++s.transmits; s.rmtBusy = true;
  s.retainedSymbols = bytes; s.retainedBytes = count; s.nonblocking = config->flags.queue_nonblocking != 0;
  return ESP_OK;
}
inline int rmt_tx_wait_all_done(rmt_channel_handle_t, int wait) {
  auto& s = io_stub::state;
  if (static_cast<unsigned>(wait) > s.maximumRmtWait) s.maximumRmtWait = static_cast<unsigned>(wait);
  return s.rmtBusy ? ESP_ERR_TIMEOUT : ESP_OK;
}
inline int nvs_open(const char*, int, nvs_handle_t* handle) { *handle = 1; return ESP_OK; }
inline int nvs_get_u8(nvs_handle_t, const char*, uint8_t* value) { *value = 1; return ESP_OK; }
inline int nvs_get_u64(nvs_handle_t, const char*, uint64_t*) { return ESP_FAIL; }
inline int nvs_get_blob(nvs_handle_t, const char*, void*, size_t*) { return ESP_FAIL; }
inline void nvs_close(nvs_handle_t) {}

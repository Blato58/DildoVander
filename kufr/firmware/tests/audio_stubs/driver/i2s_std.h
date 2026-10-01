#pragma once
#include <cstddef>
#include "gpio.h"
using esp_err_t = int;
constexpr int ESP_OK=0, ESP_FAIL=-1;
using i2s_chan_handle_t = void*;
constexpr int I2S_NUM_AUTO=0, I2S_ROLE_MASTER=0, I2S_DATA_BIT_WIDTH_32BIT=32, I2S_SLOT_MODE_STEREO=2, I2S_GPIO_UNUSED=-1;
struct i2s_chan_config_t { int dma_desc_num=0, dma_frame_num=0; bool auto_clear=false; };
#define I2S_CHANNEL_DEFAULT_CONFIG(a,b) i2s_chan_config_t{}
struct i2s_std_config_t {
  struct { unsigned rate; } clk_cfg;
  struct { int bits, mode; } slot_cfg;
  struct { int mclk, bclk, ws, dout, din; } gpio_cfg;
};
#define I2S_STD_CLK_DEFAULT_CONFIG(rate) {rate}
#define I2S_STD_PHILIPS_SLOT_DEFAULT_CONFIG(bits,mode) {bits,mode}
int i2s_new_channel(const i2s_chan_config_t*, i2s_chan_handle_t*, i2s_chan_handle_t*);
int i2s_channel_init_std_mode(i2s_chan_handle_t, const i2s_std_config_t*);
int i2s_channel_enable(i2s_chan_handle_t);
int i2s_channel_read(i2s_chan_handle_t, void*, size_t, size_t*, unsigned);
int i2s_channel_write(i2s_chan_handle_t, const void*, size_t, size_t*, unsigned);

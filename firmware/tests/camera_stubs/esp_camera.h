#pragma once
#include <stddef.h>
#include <stdint.h>
constexpr int ESP_OK = 0;
constexpr int OV5640_PID = 0x5640;
constexpr int LEDC_TIMER_0 = 0, LEDC_CHANNEL_0 = 0;
constexpr int PIXFORMAT_JPEG = 0, FRAMESIZE_VGA = 0;
constexpr int CAMERA_FB_IN_PSRAM = 0, CAMERA_GRAB_WHEN_EMPTY = 0;
struct camera_config_t {
  int pin_pwdn, pin_reset, pin_sccb_sda, pin_sccb_scl, sccb_i2c_port;
  int pin_vsync, pin_href, pin_d0, pin_d1, pin_d2, pin_d3, pin_d4, pin_d5, pin_d6, pin_d7;
  int pin_pclk, pin_xclk, xclk_freq_hz, ledc_timer, ledc_channel;
  int pixel_format, frame_size, jpeg_quality, fb_count, fb_location, grab_mode;
  size_t jpeg_buffer_size;
};
struct camera_fb_t { uint8_t* buf; size_t len, width, height; int format; };
struct sensor_t { struct { int PID; } id; };
int esp_camera_init(const camera_config_t*);
int esp_camera_deinit();
sensor_t* esp_camera_sensor_get();
camera_fb_t* esp_camera_fb_get();
void esp_camera_fb_return(camera_fb_t*);

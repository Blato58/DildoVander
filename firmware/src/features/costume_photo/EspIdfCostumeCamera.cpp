#include "EspIdfCostumeCamera.h"
#include "../physical_effects/BoardPins.h"
#include "esp_camera.h"
#include "esp_psram.h"
#include "nvs.h"
#include "sdkconfig.h"

#if !CONFIG_SCCB_HARDWARE_I2C_DRIVER_NEW || !CONFIG_SCCB_HARDWARE_I2C_PORT1
#error "Camera SCCB must use the new I2C driver on port 1; effects own port 0"
#endif

namespace {
constexpr int CameraPins[] = {4, 5, 6, 7, 11, 9, 8, 10, 12, 18, 17, 16, 13, 15};
constexpr bool PinsIndependent() {
  const auto p = KufrBoard::ProfileB;
  const int effects[] = {p.sda, p.scl, p.rgb, p.oe, p.bclk, p.ws, p.din, p.dout};
  for (unsigned i = 0; i < 14; ++i) {
    for (unsigned j = i + 1; j < 14; ++j) if (CameraPins[i] == CameraPins[j]) return false;
    for (int pin : effects) if (CameraPins[i] == pin) return false;
  }
  return true;
}
static_assert(PinsIndependent(), "OV5640 must preserve Profile B effects/audio GPIO");
void WipeFrame(camera_fb_t* frame) {
  if (!frame) return;
  volatile uint8_t* bytes = frame->buf;
  for (size_t i = 0; i < frame->len; ++i) bytes[i] = 0;
  esp_camera_fb_return(frame);
}
}

void EspIdfCostumeCamera::Configure(bool nvsReady, bool profileBVerified) {
  verified_ = false;
  nvs_handle_t handle;
  if (!nvsReady || !profileBVerified || nvs_open("wand_hw", NVS_READONLY, &handle) != ESP_OK) return;
  uint8_t enabled = 0;
  verified_ = nvs_get_u8(handle, "camera_verified", &enabled) == ESP_OK && enabled == 1;
  nvs_close(handle);
}

bool EspIdfCostumeCamera::Capture(std::vector<uint8_t>& jpeg) {
  return Capture(jpeg, []() { return true; });
}

bool EspIdfCostumeCamera::Capture(std::vector<uint8_t>& jpeg, const std::function<bool()>& current) {
  WipeCostumeJpeg(jpeg);
  if (!verified_ || !current() || !esp_psram_is_initialized()) return false;
  camera_config_t config{};
  config.pin_pwdn = -1;
  config.pin_reset = -1;
  config.pin_sccb_sda = 4;
  config.pin_sccb_scl = 5;
  // Physical effects own I2C0 on GPIO1/14. Camera SCCB owns I2C1.
  config.sccb_i2c_port = 1;
  config.pin_vsync = 6;
  config.pin_href = 7;
  config.pin_d0 = 11; config.pin_d1 = 9; config.pin_d2 = 8; config.pin_d3 = 10;
  config.pin_d4 = 12; config.pin_d5 = 18; config.pin_d6 = 17; config.pin_d7 = 16;
  config.pin_pclk = 13;
  config.pin_xclk = 15;
  config.xclk_freq_hz = 20000000;
  config.ledc_timer = LEDC_TIMER_0;
  config.ledc_channel = LEDC_CHANNEL_0;
  config.pixel_format = PIXFORMAT_JPEG;
  config.frame_size = FRAMESIZE_VGA;
  config.jpeg_quality = 16;
  config.fb_count = 1;
  config.fb_location = CAMERA_FB_IN_PSRAM;
  config.grab_mode = CAMERA_GRAB_WHEN_EMPTY;
  config.jpeg_buffer_size = MaximumCostumeJpegBytes;
  if (esp_camera_init(&config) != ESP_OK) return false;
  sensor_t* sensor = esp_camera_sensor_get();
  bool valid = current() && sensor && sensor->id.PID == OV5640_PID;
  camera_fb_t* frame = valid ? esp_camera_fb_get() : nullptr;
  valid = current() && frame && frame->format == PIXFORMAT_JPEG && frame->width <= 640 && frame->height <= 640 &&
          ValidCostumeJpeg(frame->buf, frame->len);
  if (valid) jpeg.assign(frame->buf, frame->buf + frame->len);
  WipeFrame(frame);
  // No camera producer survives this explicit single-shot request.
  const bool stopped = esp_camera_deinit() == ESP_OK;
  if (!stopped) { verified_ = false; WipeCostumeJpeg(jpeg); }
  return valid && stopped;
}

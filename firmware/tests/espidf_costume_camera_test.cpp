#include <cassert>
#include <cstring>
#include <iostream>
#include "esp_camera.h"
#include "nvs.h"
#include "../src/features/costume_photo/EspIdfCostumeCamera.h"

namespace {
bool verified = false, current = true, cancelOnInit = false;
int inits = 0, deinits = 0, gets = 0, returns = 0;
sensor_t sensor{{OV5640_PID}};
std::vector<uint8_t> frameBytes;
camera_fb_t frame{};
void ResetFrame() {
  frameBytes = {255,216,255,219,0,3,1,255,196,0,3,1,
      255,192,0,11,8,0,2,0,2,1,1,17,0,255,218,0,8,1,1,0,0,63,0,1,255,217};
  frame = {frameBytes.data(), frameBytes.size(), 2, 2, PIXFORMAT_JPEG};
}
}
bool esp_psram_is_initialized() { return true; }
int nvs_open(const char* name, int, nvs_handle_t* handle) { assert(std::strcmp(name, "wand_hw") == 0); *handle = 1; return ESP_OK; }
int nvs_get_u8(nvs_handle_t, const char* key, uint8_t* value) { assert(std::strcmp(key, "camera_verified") == 0); *value = verified ? 1 : 0; return ESP_OK; }
void nvs_close(nvs_handle_t) {}
int esp_camera_init(const camera_config_t* config) {
  ++inits;
  assert(config->pin_pwdn == -1 && config->pin_reset == -1 && config->pin_xclk == 15);
  assert(config->pin_sccb_sda == 4 && config->pin_sccb_scl == 5 && config->sccb_i2c_port == 1);
  assert(config->pin_d0 == 11 && config->pin_d1 == 9 && config->pin_d2 == 8 && config->pin_d3 == 10);
  assert(config->pin_d4 == 12 && config->pin_d5 == 18 && config->pin_d6 == 17 && config->pin_d7 == 16);
  assert(config->pin_vsync == 6 && config->pin_href == 7 && config->pin_pclk == 13);
  assert(config->fb_count == 1 && config->fb_location == CAMERA_FB_IN_PSRAM && config->frame_size == FRAMESIZE_VGA);
  assert(config->pixel_format == PIXFORMAT_JPEG && config->jpeg_buffer_size == MaximumCostumeJpegBytes);
  if (cancelOnInit) current = false;
  return ESP_OK;
}
int esp_camera_deinit() { ++deinits; return ESP_OK; }
sensor_t* esp_camera_sensor_get() { return &sensor; }
camera_fb_t* esp_camera_fb_get() { ++gets; return &frame; }
void esp_camera_fb_return(camera_fb_t* value) { ++returns; assert(value == &frame); for (auto byte : frameBytes) assert(byte == 0); }
int main() {
  EspIdfCostumeCamera camera;
  std::vector<uint8_t> jpeg;
  camera.Configure(true, true);
  assert(!camera.Available() && !camera.Capture(jpeg) && inits == 0);
  verified = true;
  camera.Configure(true, false);
  assert(!camera.Available() && inits == 0);
  camera.Configure(true, true);
  ResetFrame();
  assert(camera.Capture(jpeg, []() { return current; }) && gets == 1 && returns == 1 && deinits == 1);
  assert(ValidCostumeJpeg(jpeg.data(), jpeg.size())); WipeCostumeJpeg(jpeg);
  ResetFrame(); cancelOnInit = true;
  assert(!camera.Capture(jpeg, []() { return current; }) && gets == 1 && deinits == 2 && jpeg.empty());
  current = true; cancelOnInit = false; sensor.id.PID = 0;
  assert(!camera.Capture(jpeg) && gets == 1 && deinits == 3);
  sensor.id.PID = OV5640_PID; ResetFrame(); frame.width = 641;
  assert(!camera.Capture(jpeg) && returns == 2 && deinits == 4 && jpeg.empty());
  std::cout << "Optional OV5640 gate, GPIO/SCCB ownership, single frame and cleanup passed\n";
}

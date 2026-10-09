#include "service_config.h"
#include "esp_log.h"
#include "nvs.h"
#include "nvs_flash.h"

// A separately authorized service image, with actuator power physically OFF.
// It never configures GPIO, initializes peripherals, or erases NVS identities.
extern "C" void app_main() {
  if (nvs_flash_init() != ESP_OK) { ESP_LOGE("hw_service", "NVS unavailable; no erase attempted"); return; }
  nvs_handle_t handle;
  if (nvs_open("wand_hw", NVS_READWRITE, &handle) != ESP_OK) return;
  const char* flags[] = {"profile_b_ok", "mute_ok", "inhibit_ok", "positions_ok"};
  bool ok = true;
  // First persist a closed gate. An interrupted update cannot retain an old
  // permission while installing new calibration or wiring flags.
  for (const auto* flag : flags) ok = nvs_set_u8(handle, flag, 0) == ESP_OK && ok;
  ok = nvs_commit(handle) == ESP_OK && ok;
  if (ok) {
    ok = nvs_set_u64(handle, "cal_revision", ServiceRevision) == ESP_OK &&
         nvs_set_blob(handle, "servos", ServiceServos, sizeof(ServiceServos)) == ESP_OK &&
         nvs_commit(handle) == ESP_OK;
  }
  if (ok) {
    for (unsigned i = 0; i < 4; ++i) ok = nvs_set_u8(handle, flags[i], ServiceFlags[i]) == ESP_OK && ok;
    ok = nvs_commit(handle) == ESP_OK && ok;
  }
  if (!ok) {
    for (const auto* flag : flags) nvs_set_u8(handle, flag, 0);
    nvs_commit(handle);
  }
  nvs_close(handle);
  ESP_LOGI("hw_service", "Hardware configuration %s; restore the application image", ok ? "saved" : "failed; keep actuator power disconnected");
}

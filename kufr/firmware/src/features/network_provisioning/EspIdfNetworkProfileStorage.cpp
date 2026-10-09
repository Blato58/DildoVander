#include "EspIdfNetworkProfileStorage.h"
#include "nvs.h"

bool EspIdfNetworkProfileStorage::Load(NetworkProfileRecord& record, bool& missing) {
  missing = false;
  nvs_handle_t handle;
  const auto opened = nvs_open("wand_network", NVS_READONLY, &handle);
  if (opened == ESP_ERR_NVS_NOT_FOUND) { missing = true; return true; }
  if (opened != ESP_OK) { return false; }
  size_t length = sizeof(record);
  const auto result = nvs_get_blob(handle, "profiles_v1", &record, &length);
  nvs_close(handle);
  if (result == ESP_ERR_NVS_NOT_FOUND) { missing = true; return true; }
  return result == ESP_OK && length == sizeof(record) &&
      NetworkProfiles::ValidRecord(record);
}
bool EspIdfNetworkProfileStorage::Save(const NetworkProfileRecord& record) {
  if (!NetworkProfiles::ValidRecord(record)) { return false; }
  nvs_handle_t handle;
  if (nvs_open("wand_network", NVS_READWRITE, &handle) != ESP_OK) { return false; }
  const bool saved = nvs_set_blob(handle, "profiles_v1", &record, sizeof(record)) == ESP_OK &&
      nvs_commit(handle) == ESP_OK;
  if (saved) {
    // Only the versioned profiles own Wi-Fi credentials after successful migration.
    // Erasing the legacy keys prevents a deleted profile resurfacing on reboot.
    nvs_erase_key(handle, "ssid");
    nvs_erase_key(handle, "password");
    nvs_commit(handle);
  }
  nvs_close(handle);
  return saved;
}

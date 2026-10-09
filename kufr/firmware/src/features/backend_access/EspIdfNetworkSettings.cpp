#include "EspIdfNetworkSettings.h"

#include <array>

#include "nvs.h"

namespace {

constexpr char Namespace[] = "wand_network";

template <size_t Length>
struct SensitiveBuffer {
  ~SensitiveBuffer() {
    volatile char* bytes = data.data();
    for (size_t index = 0; index < data.size(); ++index) {
      bytes[index] = 0;
    }
  }

  std::array<char, Length> data{};
};

template <size_t Length>
bool ReadText(nvs_handle_t handle, const char* key,
              SensitiveBuffer<Length>& buffer) {
  size_t length = buffer.data.size();
  return nvs_get_str(handle, key, buffer.data.data(), &length) == ESP_OK &&
         length > 0 && length <= buffer.data.size();
}

}  // namespace

NetworkSettingsLoadResult LoadEspIdfNetworkSettings(NetworkSettings& settings,
                                                    bool nvsInitialized) {
  settings.Reset();
  if (!nvsInitialized) {
    return NetworkSettingsLoadResult::Failed;
  }

  nvs_handle_t handle = 0;
  const esp_err_t openResult = nvs_open(Namespace, NVS_READONLY, &handle);
  if (openResult == ESP_ERR_NVS_NOT_FOUND) {
    return NetworkSettingsLoadResult::Missing;
  }
  if (openResult != ESP_OK) {
    return NetworkSettingsLoadResult::Failed;
  }

  SensitiveBuffer<33> ssid;
  SensitiveBuffer<64> password;
  SensitiveBuffer<254> backendHost;
  const bool hostLoaded = ReadText(handle, "backend_host", backendHost);
  const bool legacyLoaded = ReadText(handle, "ssid", ssid) && ReadText(handle, "password", password);
  const bool valid = hostLoaded && (legacyLoaded ?
      settings.Configure(ssid.data.data(), password.data.data(), backendHost.data.data()) :
      settings.ConfigureBackend(backendHost.data.data()));
  nvs_close(handle);
  return valid ? NetworkSettingsLoadResult::Loaded
               : NetworkSettingsLoadResult::Failed;
}

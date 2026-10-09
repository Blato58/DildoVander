#include "EspIdfBleProvisioning.h"

#include <cstdlib>
#include <cstring>
#include <cstdio>
#include "esp_mac.h"
#include "esp_timer.h"
#include "nvs.h"
#include "protocomm_ble.h"
#include "protocomm_security1.h"

namespace {
constexpr uint64_t WindowMs = 300000;
constexpr uint64_t LifecycleRetryMs = 1000;
protocomm_ble_name_uuid_t Endpoints[] = {
    {"proto-ver", 0xff50}, {"prov-session", 0xff51}, {"kufr-control", 0xff54}};
const char* ProfileName(NetworkProfile profile) {
  return profile == NetworkProfile::Hotspot ? "hotspot" :
      profile == NetworkProfile::Fallback ? "fallback" : "none";
}
const char* Text(cJSON* object, const char* name) {
  const auto* value = cJSON_GetObjectItemCaseSensitive(object, name);
  return cJSON_IsString(value) ? value->valuestring : nullptr;
}
NetworkProfile ParseProfile(cJSON* request) {
  const char* name = Text(request, "profile");
  if (name == nullptr) { return NetworkProfile::None; }
  if (std::strcmp(name, "hotspot") == 0) { return NetworkProfile::Hotspot; }
  if (std::strcmp(name, "fallback") == 0) { return NetworkProfile::Fallback; }
  return NetworkProfile::None;
}
bool UniqueFields(cJSON* object) {
  for (auto* left = object->child; left != nullptr; left = left->next) {
    for (auto* right = left->next; right != nullptr; right = right->next) {
      if (left->string == nullptr || right->string == nullptr ||
          std::strcmp(left->string, right->string) == 0) { return false; }
    }
  }
  return true;
}
bool BoundedNesting(const char* input) {
  unsigned depth = 0;
  bool quoted = false;
  bool escaped = false;
  for (const char* cursor = input; *cursor != 0; ++cursor) {
    const char value = *cursor;
    if (quoted) {
      if (escaped) { escaped = false; }
      else if (value == '\\') { escaped = true; }
      else if (value == '"') { quoted = false; }
    } else if (value == '"') { quoted = true; }
    else if (value == '{' || value == '[') { if (++depth > 3) { return false; } }
    else if (value == '}' || value == ']') { if (depth == 0) { return false; } --depth; }
  }
  return depth == 0 && !quoted;
}
}

bool EspIdfBleProvisioning::Begin(bool nvsInitialized, const char* deviceId,
                                 const char* firmwareVersion, BleManagementCallbacks callbacks) {
  if (ready_ || !nvsInitialized || deviceId == nullptr || firmwareVersion == nullptr ||
      std::strlen(deviceId) > 64 || std::strlen(firmwareVersion) > 31 ||
      std::strspn(deviceId, "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_") != std::strlen(deviceId) ||
      std::strspn(firmwareVersion, "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-+") != std::strlen(firmwareVersion)) { return false; }
  nvs_handle_t handle;
  if (nvs_open("wand_ble", NVS_READONLY, &handle) != ESP_OK) { return false; }
  size_t length = sizeof(proof_);
  const auto loaded = nvs_get_str(handle, "pop", proof_, &length);
  nvs_close(handle);
  if (loaded != ESP_OK || length != 33 || proof_[32] != 0) {
    NetworkProfiles::Wipe(proof_, sizeof(proof_));
    return false;
  }
  for (size_t index = 0; index < 32; ++index) {
    if (!((proof_[index] >= '0' && proof_[index] <= '9') ||
          (proof_[index] >= 'a' && proof_[index] <= 'f'))) {
      NetworkProfiles::Wipe(proof_, sizeof(proof_));
      return false;
    }
  }
  std::strcpy(deviceId_, deviceId);
  std::strcpy(firmwareVersion_, firmwareVersion);
  uint8_t mac[6];
  if (esp_read_mac(mac, ESP_MAC_WIFI_STA) != ESP_OK) { return false; }
  std::snprintf(name_, sizeof(name_), "KUFR_%02X%02X%02X", mac[3], mac[4], mac[5]);
  proofParams_.data = reinterpret_cast<const uint8_t*>(proof_);
  proofParams_.len = 32;
  callbacks_ = callbacks;
  initialDeadline_ = esp_timer_get_time() / 1000 + WindowMs;
  initialPending_ = true;
  ready_ = true;
  if (xTaskCreate(TaskEntry, "ble_provisioning", 6144, this, 3, &lifecycleTask_) != pdPASS) {
    ready_ = false;
    NetworkProfiles::Wipe(proof_, sizeof(proof_));
    return false;
  }
  return true;
}
bool EspIdfBleProvisioning::Open(uint64_t nowMs, uint64_t windowDeadline) {
  if (!ready_ || closing_ || transport_ != nullptr || nowMs >= windowDeadline) { return false; }
  // The STA and BLE may initialize in either order, but share one default loop.
  const auto loop = esp_event_loop_create_default();
  if (loop != ESP_OK && loop != ESP_ERR_INVALID_STATE) { return false; }
  transport_ = protocomm_new();
  if (transport_ == nullptr) { return false; }
  protocomm_ble_config_t config{};
  std::strcpy(config.device_name, name_);
  const uint8_t uuid[] = {0x21,0xc8,0x95,0x63,0xd6,0x40,0xcc,0x9d,
                          0x98,0x4b,0xe8,0x70,0x10,0x7f,0x7a,0xb9};
  std::memcpy(config.service_uuid, uuid, sizeof(uuid));
  config.nu_lookup_count = 3;
  config.nu_lookup = Endpoints;
  config.keep_ble_on = false;
  if (protocomm_ble_start(transport_, &config) != ESP_OK) {
    protocomm_delete(transport_);
    transport_ = nullptr;
    return false;
  }
  const bool endpoints = protocomm_set_version(transport_, "proto-ver",
      "{\"prov\":{\"ver\":\"v1.1\",\"sec_ver\":1,\"cap\":[]},\"kufr\":{\"ver\":\"1\"}}") == ESP_OK &&
      protocomm_set_security(transport_, "prov-session", &protocomm_security1, &proofParams_) == ESP_OK &&
      protocomm_add_endpoint(transport_, "kufr-control", Handle, this) == ESP_OK;
  if (!endpoints) { Close(nowMs); return false; }
  deadline_.store(windowDeadline);
  active_.store(true);
  return true;
}
void EspIdfBleProvisioning::Close(uint64_t nowMs) {
  active_.store(false);
  deadline_.store(0);
  if (transport_ != nullptr) {
    // Stop drains the transport before handler data and security are released.
    closing_ = true;
    if (protocomm_ble_stop(transport_) != ESP_OK) {
      nextCloseRetry_ = nowMs + LifecycleRetryMs;
      return;
    }
    protocomm_delete(transport_);
    transport_ = nullptr;
  }
  closing_ = false;
}
void EspIdfBleProvisioning::Tick(uint64_t nowMs, bool networkReady) {
  (void)nowMs;
  networkReady_.store(networkReady);
}
void EspIdfBleProvisioning::TaskEntry(void* context) {
  static_cast<EspIdfBleProvisioning*>(context)->Run();
  vTaskDelete(nullptr);
}
void EspIdfBleProvisioning::Run() {
  while (true) {
    UpdateWindow(esp_timer_get_time() / 1000, networkReady_.load());
    vTaskDelay(pdMS_TO_TICKS(100));
  }
}
void EspIdfBleProvisioning::UpdateWindow(uint64_t nowMs, bool networkReady) {
  if (!ready_) { return; }
  if (closing_) {
    if (nowMs >= nextCloseRetry_) { Close(nowMs); }
  } else if (active_.load() && nowMs >= deadline_.load()) { Close(nowMs); }
  // Startup retries are independent of Wi-Fi and cannot extend the boot window.
  if (initialPending_ && nowMs >= initialDeadline_) { initialPending_ = false; }
  if (initialPending_ && !closing_ && nowMs >= nextOpenAttempt_) {
    nextOpenAttempt_ = nowMs + LifecycleRetryMs;
    if (Open(nowMs, initialDeadline_)) { initialPending_ = false; }
  }
  if (networkReady) { offlineTracked_ = false; return; }
  if (!offlineTracked_) { offlineTracked_ = true; offlineSince_ = nowMs; }
  // A failed stop retains transport ownership. Retry without consuming the one
  // recovery window, and never start a second radio against that stale transport.
  if (!initialPending_ && !closing_ && !active_.load() && !recoveryUsed_ &&
      nowMs - offlineSince_ >= 60000 && nowMs >= nextOpenAttempt_) {
    nextOpenAttempt_ = nowMs + LifecycleRetryMs;
    if (Open(nowMs, nowMs + WindowMs)) { recoveryUsed_ = true; }
  }
}
void EspIdfBleProvisioning::AddStatus(cJSON* response) {
  cJSON_AddStringToObject(response, "id", deviceId_);
  cJSON_AddStringToObject(response, "fw", firmwareVersion_);
  const auto status = station_.Status();
  auto* network = cJSON_AddObjectToObject(response, "n");
  cJSON_AddStringToObject(network, "p", status.phase);
  cJSON_AddStringToObject(network, "r", status.result);
  cJSON_AddStringToObject(network, "a", ProfileName(status.active));
  cJSON_AddStringToObject(network, "l", ProfileName(status.lastSuccessful));
  cJSON_AddStringToObject(network, "h", status.hotspotSsid);
  cJSON_AddStringToObject(network, "f", status.fallbackSsid);
  if (callbacks_.status != nullptr) { callbacks_.status(response, callbacks_.context); }
  if (cJSON_GetObjectItemCaseSensitive(response, "b") == nullptr) {
    cJSON_AddBoolToObject(response, "b", false);
  }
  if (cJSON_GetObjectItemCaseSensitive(response, "o") == nullptr) {
    auto* ota = cJSON_AddObjectToObject(response, "o");
    cJSON_AddStringToObject(ota, "s", "unavailable");
    cJSON_AddBoolToObject(ota, "i", false);
  }
}
cJSON* EspIdfBleProvisioning::Execute(cJSON* request) {
  auto* response = cJSON_CreateObject();
  if (response == nullptr) { return nullptr; }
  cJSON_AddNumberToObject(response, "v", 1);
  const auto* version = cJSON_GetObjectItemCaseSensitive(request, "v");
  const char* command = Text(request, "command");
  bool accepted = false;
  const char* error = "invalid_request";
  if (cJSON_IsObject(request) && UniqueFields(request) && cJSON_IsNumber(version) && version->valuedouble == 1 && command != nullptr) {
    const auto profile = ParseProfile(request);
    if (std::strcmp(command, "status") == 0) { accepted = true; }
    else if (std::strcmp(command, "configure") == 0) {
      const char* ssid = Text(request, "ssid");
      const char* password = Text(request, "password");
      WifiProfile candidate;
      if (NetworkProfiles::MakeCandidate(ssid, password, candidate) && profile != NetworkProfile::None) {
        accepted = station_.Configure(profile, ssid, password);
        error = "busy";
      }
      NetworkProfiles::Wipe(&candidate, sizeof(candidate));
    } else if (std::strcmp(command, "delete") == 0 &&
               cJSON_IsTrue(cJSON_GetObjectItemCaseSensitive(request, "confirm")) && profile != NetworkProfile::None) {
      accepted = station_.Delete(profile);
      error = "busy";
    } else if (std::strcmp(command, "test") == 0 && profile != NetworkProfile::None) {
      accepted = station_.Test(profile);
      error = "busy";
    } else if (std::strcmp(command, "ota-check") == 0 || std::strcmp(command, "ota-install") == 0 ||
               (std::strcmp(command, "ota-policy") == 0 && cJSON_IsBool(cJSON_GetObjectItemCaseSensitive(request, "automaticInstall")))) {
      accepted = callbacks_.command != nullptr && callbacks_.command(command,
          cJSON_IsTrue(cJSON_GetObjectItemCaseSensitive(request, "automaticInstall")), callbacks_.context);
      error = "busy";
    }
  }
  cJSON_AddBoolToObject(response, "ok", accepted);
  if (!accepted) { cJSON_AddStringToObject(response, "error", error); }
  AddStatus(response);
  return response;
}
esp_err_t EspIdfBleProvisioning::Handle(uint32_t, const uint8_t* input, ssize_t inputLength,
                                       uint8_t** output, ssize_t* outputLength, void* context) {
  auto* owner = static_cast<EspIdfBleProvisioning*>(context);
  if (!owner->active_.load() || static_cast<uint64_t>(esp_timer_get_time() / 1000) >= owner->deadline_.load() ||
      input == nullptr || inputLength <= 0 || inputLength > 512) { return ESP_ERR_INVALID_ARG; }
  // Bounded exact parse: no trailing data, embedded NUL or decoded NUL fields.
  char buffer[513];
  std::memcpy(buffer, input, inputLength);
  buffer[inputLength] = 0;
  if (std::memchr(buffer, 0, inputLength) != nullptr || std::strstr(buffer, "\\u0000") != nullptr ||
      !BoundedNesting(buffer)) {
    NetworkProfiles::Wipe(buffer, sizeof(buffer));
    return ESP_ERR_INVALID_ARG;
  }
  auto* request = cJSON_ParseWithOpts(buffer, nullptr, true);
  auto* response = request != nullptr ? owner->Execute(request) : nullptr;
  if (request != nullptr) {
    auto* password = cJSON_GetObjectItemCaseSensitive(request, "password");
    if (cJSON_IsString(password)) { NetworkProfiles::Wipe(password->valuestring, std::strlen(password->valuestring)); }
  }
  cJSON_Delete(request);
  NetworkProfiles::Wipe(buffer, sizeof(buffer));
  if (response == nullptr) { return ESP_ERR_INVALID_ARG; }
  char* serialized = cJSON_PrintUnformatted(response);
  cJSON_Delete(response);
  if (serialized == nullptr) { return ESP_ERR_NO_MEM; }
  const size_t length = std::strlen(serialized);
  if (length > 512) { cJSON_free(serialized); return ESP_ERR_INVALID_SIZE; }
  *output = reinterpret_cast<uint8_t*>(serialized);
  *outputLength = length;
  return ESP_OK;
}

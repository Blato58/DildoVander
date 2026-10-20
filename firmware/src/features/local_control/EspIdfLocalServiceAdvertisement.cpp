#include "EspIdfLocalServiceAdvertisement.h"

#include "esp_timer.h"
#include "mdns.h"
#include "LocalControlCapabilities.h"
#include "../backend_access/EspIdfStationNetwork.h"

namespace {
bool ValidDeviceId(const std::string& value) {
  if (value.empty() || value.size() > 64 || value.front() < 'a' || value.front() > 'z') {
    return false;
  }
  for (char character : value) {
    if (!((character >= 'a' && character <= 'z') ||
          (character >= '0' && character <= '9') || character == '-')) {
      return false;
    }
  }
  return true;
}
}

EspIdfLocalServiceAdvertisement::EspIdfLocalServiceAdvertisement(
    EspIdfStationNetwork& station, Sha256Port& hasher)
    : station_(station), hasher_(hasher) {}

bool EspIdfLocalServiceAdvertisement::Start(const std::string& deviceId) {
  if (task_ != nullptr || !SupportsLocalControl(deviceId)) {
    return false;
  }
  hostname_ = Hostname(deviceId, hasher_);
  if (hostname_.empty()) { return false; }
  deviceId_ = deviceId;
  if (xTaskCreate(TaskEntry, "local_discovery", 4096, this, 3, &task_) != pdPASS) {
    task_ = nullptr;
    return false;
  }
  return true;
}

bool EspIdfLocalServiceAdvertisement::SupportsLocalControl(const std::string& deviceId) {
  return ValidDeviceId(deviceId);
}

std::string EspIdfLocalServiceAdvertisement::Hostname(
    const std::string& deviceId, Sha256Port& hasher) {
  // This also names boot-generated TLS identities. Provisioned backend IDs
  // predate the narrower control-v1 grammar (e.g. kufr_1). Only the digest is
  // used in DNS/certificates; protocol validation stays at advertisement Start.
  if (deviceId.empty() || deviceId.size() > DeviceIdentity::MaximumDeviceIdLength) { return ""; }
  std::array<uint8_t, 32> digest{};
  if (!hasher.Hash(reinterpret_cast<const uint8_t*>(deviceId.data()), deviceId.size(), digest)) {
    return "";
  }
  std::string hostname = "kufr-";
  constexpr char Hex[] = "0123456789abcdef";
  // Public device ID hash gives a bounded DNS label even for 64-byte IDs.
  for (size_t index = 0; index < 16; ++index) {
    hostname += Hex[digest[index] >> 4];
    hostname += Hex[digest[index] & 15];
  }
  return hostname;
}

void EspIdfLocalServiceAdvertisement::TaskEntry(void* context) {
  static_cast<EspIdfLocalServiceAdvertisement*>(context)->Run();
  vTaskDelete(nullptr);
}

void EspIdfLocalServiceAdvertisement::Run() {
  while (true) {
    // A generation also detects a brief IP loss/replacement between samples.
    const uint32_t generation = station_.IpGeneration();
    Update(static_cast<uint64_t>(esp_timer_get_time() / 1000), station_.HasIp(), generation);
    vTaskDelay(pdMS_TO_TICKS(100));
  }
}

void EspIdfLocalServiceAdvertisement::Update(uint64_t nowMs, bool hasIp, uint32_t generation) {
  if (!hasIp || generation != generation_) {
    Unpublish();
    generation_ = generation;
    retryAtMs_ = 0;
  }
  if (!hasIp || initialized_ || nowMs < retryAtMs_) {
    return;
  }
  retryAtMs_ = nowMs + 5000;
  if (mdns_init() != ESP_OK) {
    return;
  }
  initialized_ = true;
  mdns_txt_item_t txt[] = {
      {"deviceId", deviceId_.c_str()},
      {"protocol", "1"},
      {"capabilities", LocalControlCapabilities::Csv},
  };
  if (mdns_hostname_set(hostname_.c_str()) != ESP_OK ||
      mdns_service_add(hostname_.c_str(), "_kufr", "_tcp", 443, txt, 3) != ESP_OK) {
    Unpublish();
  }
}

void EspIdfLocalServiceAdvertisement::Unpublish() {
  if (initialized_) {
    mdns_free();
    initialized_ = false;
  }
}

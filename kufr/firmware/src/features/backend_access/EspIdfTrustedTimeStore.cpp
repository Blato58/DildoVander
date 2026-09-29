#include "EspIdfTrustedTimeStore.h"

namespace {

constexpr char Namespace[] = "wand_clock";
constexpr char LastTrustedKey[] = "trusted_utc";

}  // namespace

EspIdfTrustedTimeStore::EspIdfTrustedTimeStore(bool nvsInitialized) {
  if (nvsInitialized &&
      nvs_open(Namespace, NVS_READWRITE, &handle_) == ESP_OK) {
    opened_ = true;
  }
}

EspIdfTrustedTimeStore::~EspIdfTrustedTimeStore() {
  if (opened_) {
    nvs_close(handle_);
  }
}

LastTrustedTimeRead EspIdfTrustedTimeStore::Read(uint64_t& epochSeconds) {
  if (!opened_) {
    return LastTrustedTimeRead::Failed;
  }
  const esp_err_t result = nvs_get_u64(handle_, LastTrustedKey,
                                       &epochSeconds);
  if (result == ESP_ERR_NVS_NOT_FOUND) {
    return LastTrustedTimeRead::Missing;
  }
  return result == ESP_OK ? LastTrustedTimeRead::Found
                          : LastTrustedTimeRead::Failed;
}

bool EspIdfTrustedTimeStore::Write(uint64_t epochSeconds) {
  return opened_ &&
         nvs_set_u64(handle_, LastTrustedKey, epochSeconds) == ESP_OK &&
         nvs_commit(handle_) == ESP_OK;
}

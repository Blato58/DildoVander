#include "EspIdfProvisioningStorage.h"

namespace {

constexpr char Namespace[] = "wand_identity";
constexpr char IdentityKey[] = "device";

}  // namespace

EspIdfProvisioningStorage::EspIdfProvisioningStorage(bool nvsInitialized) {
  if (nvsInitialized &&
      nvs_open(Namespace, NVS_READWRITE, &handle_) == ESP_OK) {
    opened_ = true;
  }
}

EspIdfProvisioningStorage::~EspIdfProvisioningStorage() {
  if (opened_) {
    nvs_close(handle_);
  }
}

ProvisioningReadResult EspIdfProvisioningStorage::Read(uint8_t* data,
                                                       size_t capacity,
                                                       size_t& length) {
  if (!opened_) {
    return ProvisioningReadResult::Failed;
  }

  size_t storedLength = 0;
  const esp_err_t sizeResult = nvs_get_blob(handle_, IdentityKey, nullptr,
                                            &storedLength);
  if (sizeResult == ESP_ERR_NVS_NOT_FOUND) {
    return ProvisioningReadResult::Missing;
  }
  if (sizeResult != ESP_OK || storedLength > capacity) {
    return ProvisioningReadResult::Failed;
  }

  length = storedLength;
  return nvs_get_blob(handle_, IdentityKey, data, &length) == ESP_OK
             ? ProvisioningReadResult::Found
             : ProvisioningReadResult::Failed;
}

bool EspIdfProvisioningStorage::Write(const uint8_t* data, size_t length) {
  return opened_ && nvs_set_blob(handle_, IdentityKey, data, length) == ESP_OK &&
         nvs_commit(handle_) == ESP_OK;
}

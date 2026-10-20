#include "EspIdfLocalPairingStorage.h"

namespace {

// The backend device credential lives in wand_identity/device.
constexpr char Namespace[] = "wand_pairing";
constexpr char PairingKey[] = "local";

}  // namespace

EspIdfLocalPairingStorage::EspIdfLocalPairingStorage(bool nvsInitialized) {
  if (nvsInitialized &&
      nvs_open(Namespace, NVS_READWRITE, &handle_) == ESP_OK) {
    opened_ = true;
  }
}

EspIdfLocalPairingStorage::~EspIdfLocalPairingStorage() {
  if (opened_) {
    nvs_close(handle_);
  }
}

ProvisioningReadResult EspIdfLocalPairingStorage::Read(
    uint8_t* data, size_t capacity, size_t& length) {
  length = 0;
  if (!opened_ || data == nullptr) {
    return ProvisioningReadResult::Failed;
  }

  size_t storedLength = 0;
  const esp_err_t sizeResult =
      nvs_get_blob(handle_, PairingKey, nullptr, &storedLength);
  if (sizeResult == ESP_ERR_NVS_NOT_FOUND) {
    return ProvisioningReadResult::Missing;
  }
  if (sizeResult != ESP_OK || storedLength != capacity) {
    return ProvisioningReadResult::Failed;
  }

  length = storedLength;
  return nvs_get_blob(handle_, PairingKey, data, &length) == ESP_OK &&
                 length == storedLength
             ? ProvisioningReadResult::Found
             : ProvisioningReadResult::Failed;
}

bool EspIdfLocalPairingStorage::Write(const uint8_t* data, size_t length) {
  return opened_ && data != nullptr &&
         length == LocalPairingCredential::RecordSize &&
         nvs_set_blob(handle_, PairingKey, data, length) == ESP_OK &&
         nvs_commit(handle_) == ESP_OK;
}

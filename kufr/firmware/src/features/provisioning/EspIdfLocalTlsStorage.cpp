#include "EspIdfLocalTlsStorage.h"

#include "LocalTlsIdentity.h"

namespace {

constexpr char Namespace[] = "wand_tls";
constexpr char IdentityKey[] = "server";

}  // namespace

EspIdfLocalTlsStorage::EspIdfLocalTlsStorage(bool nvsInitialized) {
  if (nvsInitialized &&
      nvs_open(Namespace, NVS_READWRITE, &handle_) == ESP_OK) {
    opened_ = true;
  }
}

EspIdfLocalTlsStorage::~EspIdfLocalTlsStorage() {
  if (opened_) {
    nvs_close(handle_);
  }
}

ProvisioningReadResult EspIdfLocalTlsStorage::Read(
    uint8_t* data, size_t capacity, size_t& length) {
  length = 0;
  if (!opened_ || data == nullptr) {
    return ProvisioningReadResult::Failed;
  }
  size_t storedLength = 0;
  const esp_err_t result =
      nvs_get_blob(handle_, IdentityKey, nullptr, &storedLength);
  if (result == ESP_ERR_NVS_NOT_FOUND) {
    return ProvisioningReadResult::Missing;
  }
  if (result != ESP_OK || storedLength < LocalTlsIdentity::HeaderSize ||
      storedLength > capacity ||
      storedLength > LocalTlsIdentity::MaximumRecordSize) {
    return ProvisioningReadResult::Failed;
  }
  length = storedLength;
  return nvs_get_blob(handle_, IdentityKey, data, &length) == ESP_OK &&
                 length == storedLength
             ? ProvisioningReadResult::Found
             : ProvisioningReadResult::Failed;
}

bool EspIdfLocalTlsStorage::Write(const uint8_t* data, size_t length) {
  return opened_ && data != nullptr &&
         length >= LocalTlsIdentity::HeaderSize &&
         length <= LocalTlsIdentity::MaximumRecordSize &&
         nvs_set_blob(handle_, IdentityKey, data, length) == ESP_OK &&
         nvs_commit(handle_) == ESP_OK;
}

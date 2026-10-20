#include "EspIdfJournalIdentityStorage.h"
#include "JournalIdentityOwner.h"

namespace {
constexpr char Namespace[] = "wand_identity";
constexpr char MarkerKey[] = "journal_scope";
}

EspIdfJournalIdentityStorage::EspIdfJournalIdentityStorage(bool nvsInitialized) {
  if (nvsInitialized && nvs_open(Namespace, NVS_READWRITE, &handle_) == ESP_OK) {
    opened_ = true;
    usable_ = true;
  }
}

EspIdfJournalIdentityStorage::~EspIdfJournalIdentityStorage() {
  if (opened_) nvs_close(handle_);
}

JournalReadResult EspIdfJournalIdentityStorage::Read(uint8_t* data, size_t capacity,
                                                   size_t& length) {
  length = 0;
  if (!usable_ || data == nullptr) return JournalReadResult::Failed;
  size_t storedLength = 0;
  const esp_err_t result = nvs_get_blob(handle_, MarkerKey, nullptr, &storedLength);
  if (result == ESP_ERR_NVS_NOT_FOUND) return JournalReadResult::Missing;
  if (result != ESP_OK || storedLength == 0 || storedLength > capacity ||
      storedLength > JournalIdentityOwner::MaximumRecordSize) {
    usable_ = false;
    return JournalReadResult::Failed;
  }
  length = storedLength;
  if (nvs_get_blob(handle_, MarkerKey, data, &length) != ESP_OK || length != storedLength) {
    usable_ = false;
    length = 0;
    return JournalReadResult::Failed;
  }
  return JournalReadResult::Found;
}

bool EspIdfJournalIdentityStorage::Write(const uint8_t* data, size_t length) {
  if (!usable_ || data == nullptr || length == 0 || length > JournalIdentityOwner::MaximumRecordSize) return false;
  if (nvs_set_blob(handle_, MarkerKey, data, length) != ESP_OK || nvs_commit(handle_) != ESP_OK) {
    usable_ = false;
    return false;
  }
  return true;
}

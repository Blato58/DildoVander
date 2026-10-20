#include "EspIdfOfflineOutboxStorage.h"

namespace kufr { namespace offline {

EspIdfOutboxStorage::BlobStorage::BlobStorage(bool initialized) : initialized_(initialized) { Reopen(); }
EspIdfOutboxStorage::BlobStorage::~BlobStorage() {
  if (opened_) nvs_close(handle_);
}
bool EspIdfOutboxStorage::BlobStorage::Reopen() {
  if (opened_) nvs_close(handle_);
  handle_ = 0; opened_ = false; usable_ = false;
  opened_ = initialized_ && nvs_open("offline_show", NVS_READWRITE, &handle_) == ESP_OK;
  usable_ = opened_;
  return usable_;
}
JournalReadResult EspIdfOutboxStorage::BlobStorage::Read(uint8_t* bytes, size_t capacity, size_t& length) {
  length = 0;
  if (!usable_ || !bytes) return JournalReadResult::Failed;
  size_t storedLength = 0;
  const auto result = nvs_get_blob(handle_, "outbox", nullptr, &storedLength);
  if (result == ESP_ERR_NVS_NOT_FOUND) return JournalReadResult::Missing;
  if (result != ESP_OK || storedLength == 0 || storedLength > capacity ||
      storedLength > OfflineCommittedInventory::MaximumRecordSize) {
    usable_ = false;
    return JournalReadResult::Failed;
  }
  length = storedLength;
  if (nvs_get_blob(handle_, "outbox", bytes, &length) != ESP_OK || length != storedLength) {
    usable_ = false; length = 0;
    return JournalReadResult::Failed;
  }
  return JournalReadResult::Found;
}
bool EspIdfOutboxStorage::BlobStorage::Write(const uint8_t* bytes, size_t length) {
  if (!usable_ || !bytes || length == 0 || length > OfflineCommittedInventory::MaximumRecordSize) return false;
  if (nvs_set_blob(handle_, "outbox", bytes, length) != ESP_OK || nvs_commit(handle_) != ESP_OK) {
    // A failed ACK may hide a commit or leave a handle-local pending blob. No
    // subsequent read/write may use that handle before explicit close/reopen.
    usable_ = false;
    return false;
  }
  return true;
}
EspIdfOutboxStorage::EspIdfOutboxStorage(bool initialized) : bytes_(initialized), inventory_(bytes_) {}
bool EspIdfOutboxStorage::Read(Outbox& outbox) { return inventory_.Read(outbox); }
bool EspIdfOutboxStorage::Write(const Outbox& outbox) { return inventory_.Write(outbox); }
bool EspIdfOutboxStorage::ReadCommitted(JournalEvidenceInventory& inventory) const {
  return inventory_.ReadCommitted(inventory);
}
bool EspIdfOutboxStorage::EstablishBinding(const std::string& deviceId, bool serviceAuthorized) {
  return inventory_.EstablishBinding(deviceId, serviceAuthorized);
}
bool EspIdfOutboxStorage::Reopen() {
  bytes_.Reopen();
  return inventory_.Reopen();
}

} }

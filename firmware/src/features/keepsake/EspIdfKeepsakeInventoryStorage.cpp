#include "EspIdfKeepsakeInventoryStorage.h"

#include "nvs.h"
#include "KeepsakeCommittedInventory.h"

namespace {
constexpr char Namespace[] = "dv10_outbox";
constexpr char Key[] = "completed";
}

JournalReadResult EspIdfKeepsakeInventoryStorage::Read(uint8_t* bytes, size_t capacity, size_t& length) {
  length = 0;
  if (!initialized_ || !bytes || !capacity) return JournalReadResult::Failed;
  nvs_handle_t handle;
  const auto opened = nvs_open(Namespace, NVS_READONLY, &handle);
  if (opened == ESP_ERR_NVS_NOT_FOUND) return JournalReadResult::Missing;
  if (opened != ESP_OK) return JournalReadResult::Failed;
  size_t size = 0;
  const auto result = nvs_get_blob(handle, Key, nullptr, &size);
  if (result == ESP_ERR_NVS_NOT_FOUND) { nvs_close(handle); return JournalReadResult::Missing; }
  const bool valid = result == ESP_OK && size && size <= capacity
      && size <= KeepsakeCommittedInventory::MaximumRecordSize
      && nvs_get_blob(handle, Key, bytes, &size) == ESP_OK;
  nvs_close(handle);
  if (!valid || !size || size > capacity) return JournalReadResult::Failed;
  length = size;
  return JournalReadResult::Found;
}

bool EspIdfKeepsakeInventoryStorage::Write(const uint8_t* bytes, size_t length) {
  if (!initialized_ || !bytes || !length || length > KeepsakeCommittedInventory::MaximumRecordSize) return false;
  nvs_handle_t handle;
  if (nvs_open(Namespace, NVS_READWRITE, &handle) != ESP_OK) return false;
  const bool stored = nvs_set_blob(handle, Key, bytes, length) == ESP_OK && nvs_commit(handle) == ESP_OK;
  nvs_close(handle);
  return stored;
}

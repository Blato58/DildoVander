#include "EspIdfJournalStorage.h"

namespace {

constexpr char Namespace[] = "wand_journal";
constexpr char StateKey[] = "state";

}  // namespace

EspIdfJournalStorage::EspIdfJournalStorage(bool nvsInitialized) {
  if (nvsInitialized &&
      nvs_open(Namespace, NVS_READWRITE, &handle_) == ESP_OK) {
    opened_ = true;
  }
}

EspIdfJournalStorage::~EspIdfJournalStorage() {
  if (opened_) {
    nvs_close(handle_);
  }
}

JournalReadResult EspIdfJournalStorage::Read(uint8_t* data, size_t capacity,
                                            size_t& length) {
  if (!opened_) {
    return JournalReadResult::Failed;
  }

  size_t storedLength = 0;
  const esp_err_t sizeResult = nvs_get_blob(handle_, StateKey, nullptr,
                                            &storedLength);
  if (sizeResult == ESP_ERR_NVS_NOT_FOUND) {
    return JournalReadResult::Missing;
  }
  if (sizeResult != ESP_OK || storedLength > capacity) {
    return JournalReadResult::Failed;
  }

  length = storedLength;
  return nvs_get_blob(handle_, StateKey, data, &length) == ESP_OK
             ? JournalReadResult::Found
             : JournalReadResult::Failed;
}

bool EspIdfJournalStorage::Write(const uint8_t* data, size_t length) {
  return opened_ && nvs_set_blob(handle_, StateKey, data, length) == ESP_OK &&
         nvs_commit(handle_) == ESP_OK;
}

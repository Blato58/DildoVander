#include <cassert>
#include <cstring>
#include <iostream>
#include <vector>

#include "../src/features/device_control/EspIdfJournalIdentityStorage.h"
#include "../src/features/device_control/JournalIdentityOwner.h"

namespace {
std::vector<uint8_t> committed;
std::vector<uint8_t> staged;
bool openFails = false;
bool readFails = false;
bool setFails = false;
bool commitFails = false;
bool loseAcknowledgement = false;
unsigned closes = 0;
unsigned sets = 0;
unsigned dataReads = 0;

void Reset() {
  committed.clear();
  staged.clear();
  openFails = readFails = setFails = commitFails = loseAcknowledgement = false;
  closes = sets = dataReads = 0;
}
}

esp_err_t nvs_open(const char* name, int mode, nvs_handle_t* handle) {
  assert(std::strcmp(name, "wand_identity") == 0 && mode == NVS_READWRITE);
  if (openFails) return ESP_FAIL;
  // Closing/reopening never treats an earlier uncommitted write as authority.
  staged = committed;
  *handle = 1;
  return ESP_OK;
}

void nvs_close(nvs_handle_t handle) { assert(handle == 1); ++closes; }

esp_err_t nvs_get_blob(nvs_handle_t handle, const char* key, void* data, size_t* length) {
  assert(handle == 1 && std::strcmp(key, "journal_scope") == 0);
  if (readFails) return ESP_FAIL;
  if (committed.empty()) return ESP_ERR_NVS_NOT_FOUND;
  if (data == nullptr) { *length = committed.size(); return ESP_OK; }
  ++dataReads;
  if (*length < committed.size()) return ESP_FAIL;
  std::memcpy(data, committed.data(), committed.size());
  *length = committed.size();
  return ESP_OK;
}

esp_err_t nvs_set_blob(nvs_handle_t handle, const char* key, const void* data, size_t length) {
  assert(handle == 1 && std::strcmp(key, "journal_scope") == 0);
  ++sets;
  if (setFails) return ESP_FAIL;
  const auto* bytes = static_cast<const uint8_t*>(data);
  staged.assign(bytes, bytes + length);
  return ESP_OK;
}

esp_err_t nvs_commit(nvs_handle_t handle) {
  assert(handle == 1);
  if (commitFails) return ESP_FAIL;
  committed = staged;
  return loseAcknowledgement ? ESP_FAIL : ESP_OK;
}

int main() {
  uint8_t output[8]{};
  const uint8_t oldRecord[] = {1, 2, 3};
  const uint8_t newRecord[] = {4, 5, 6, 7};
  size_t length = 99;
  Reset();
  {
    EspIdfJournalIdentityStorage unavailable(false);
    assert(unavailable.Read(output, sizeof(output), length) == JournalReadResult::Failed && length == 0);
    assert(!unavailable.Write(oldRecord, sizeof(oldRecord)) && sets == 0);
  }
  assert(closes == 0);
  openFails = true;
  {
    EspIdfJournalIdentityStorage unavailable(true);
    assert(unavailable.Read(output, sizeof(output), length) == JournalReadResult::Failed);
  }
  assert(closes == 0);
  openFails = false;
  {
    EspIdfJournalIdentityStorage storage(true);
    assert(storage.Read(output, sizeof(output), length) == JournalReadResult::Missing && length == 0);
    assert(storage.Write(oldRecord, sizeof(oldRecord)));
    const unsigned writes = sets;
    assert(!storage.Write(oldRecord, JournalIdentityOwner::MaximumRecordSize + 1) && sets == writes);
    assert(storage.Read(output, sizeof(output), length) == JournalReadResult::Found && length == sizeof(oldRecord));
    assert(std::memcmp(output, oldRecord, length) == 0);
  }
  assert(closes == 1);
  {
    std::vector<uint8_t> oversized(JournalIdentityOwner::MaximumRecordSize + 1, 1);
    committed = oversized;
    EspIdfJournalIdentityStorage storage(true);
    const unsigned reads = dataReads;
    assert(storage.Read(oversized.data(), oversized.size(), length) == JournalReadResult::Failed
        && length == 0 && dataReads == reads);
    committed.assign(oldRecord, oldRecord + sizeof(oldRecord));
  }
  {
    EspIdfJournalIdentityStorage storage(true);
    assert(storage.Read(output, 2, length) == JournalReadResult::Failed && length == 0);
    const unsigned reads = dataReads;
    assert(storage.Read(output, sizeof(output), length) == JournalReadResult::Failed && dataReads == reads);
    assert(!storage.Write(newRecord, sizeof(newRecord)));
  }
  for (unsigned fault = 0; fault < 3; ++fault) {
    committed.assign(oldRecord, oldRecord + sizeof(oldRecord));
    {
      EspIdfJournalIdentityStorage storage(true);
      setFails = fault == 0;
      commitFails = fault == 1;
      loseAcknowledgement = fault == 2;
      assert(!storage.Write(newRecord, sizeof(newRecord)));
      const unsigned writes = sets;
      assert(!storage.Write(oldRecord, sizeof(oldRecord)) && sets == writes);
      assert(storage.Read(output, sizeof(output), length) == JournalReadResult::Failed && length == 0);
    }
    setFails = commitFails = loseAcknowledgement = false;
    EspIdfJournalIdentityStorage reopened(true);
    assert(reopened.Read(output, sizeof(output), length) == JournalReadResult::Found);
    const auto* expected = fault == 2 ? newRecord : oldRecord;
    const size_t expectedLength = fault == 2 ? sizeof(newRecord) : sizeof(oldRecord);
    assert(length == expectedLength && std::memcmp(output, expected, length) == 0);
  }
  {
    EspIdfJournalIdentityStorage storage(true);
    readFails = true;
    assert(storage.Read(output, sizeof(output), length) == JournalReadResult::Failed);
    readFails = false;
    assert(!storage.Write(oldRecord, sizeof(oldRecord)));
  }
  std::cout << "Journal identity NVS adapter: namespace, bounds, failures and committed lost-ACK reopen passed.\n";
}

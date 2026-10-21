#include <algorithm>
#include <cassert>
#include <cstring>
#include <iostream>
#include <vector>

#include "nvs.h"
#include "../src/features/keepsake/EspIdfKeepsakeInventoryStorage.h"
#include "../src/features/keepsake/KeepsakeCommittedInventory.h"

namespace {
enum class CommitCut { None, Before, After };
struct NvsState {
  bool nameExists = false, keyExists = false;
  bool failOpen = false, failSize = false, failRead = false, failSet = false;
  CommitCut commitCut = CommitCut::None;
  unsigned opens = 0, closes = 0, sizes = 0, reads = 0, sets = 0, commits = 0;
  std::vector<uint8_t> committed, staged;
} nvs;
constexpr char Device[] = "nvs-online";
KeepsakeOutcome Candidate() {
  KeepsakeOutcome item{"11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222",
      "33333333-3333-4333-8333-333333333333", "33333333-3333-4333-8333-333333333333",
      "44444444-4444-4444-8444-444444444444", 1, 2};
  item.compartment = 3; item.operationEpoch = 1; item.physicalFingerprint = 101;
  return item;
}
void MissingBoundsAndReadFailures() {
  nvs = {};
  EspIdfKeepsakeInventoryStorage unavailable(false), storage(true);
  size_t length = 99;
  std::vector<uint8_t> bytes(KeepsakeCommittedInventory::MaximumRecordSize + 1, 0);
  assert(unavailable.Read(bytes.data(), bytes.size(), length) == JournalReadResult::Failed && length == 0);
  assert(!unavailable.Write(bytes.data(), 1) && !nvs.opens);
  assert(storage.Read(bytes.data(), bytes.size(), length) == JournalReadResult::Missing && !nvs.nameExists);
  assert(!storage.Write(nullptr, 1) && !storage.Write(bytes.data(), 0));
  const auto opens = nvs.opens;
  assert(!storage.Write(bytes.data(), bytes.size()) && nvs.opens == opens);
  assert(storage.Write(bytes.data(), KeepsakeCommittedInventory::MaximumRecordSize));
  assert(nvs.committed.size() == KeepsakeCommittedInventory::MaximumRecordSize && nvs.commits == 1);
  assert(storage.Read(bytes.data(), bytes.size(), length) == JournalReadResult::Found
      && length == KeepsakeCommittedInventory::MaximumRecordSize);
  nvs.committed.push_back(0);
  const auto reads = nvs.reads;
  assert(storage.Read(bytes.data(), bytes.size(), length) == JournalReadResult::Failed && !length && nvs.reads == reads);
  nvs.committed.assign(5, 1);
  assert(storage.Read(bytes.data(), 4, length) == JournalReadResult::Failed && !length && nvs.reads == reads);
  nvs.failOpen = true;
  assert(storage.Read(bytes.data(), bytes.size(), length) == JournalReadResult::Failed && !length);
  assert(!storage.Write(bytes.data(), 1)); nvs.failOpen = false;
  nvs.failSize = true;
  assert(storage.Read(bytes.data(), bytes.size(), length) == JournalReadResult::Failed && !length); nvs.failSize = false;
  nvs.failRead = true;
  assert(storage.Read(bytes.data(), bytes.size(), length) == JournalReadResult::Failed && !length); nvs.failRead = false;
  nvs.keyExists = false;
  assert(storage.Read(bytes.data(), bytes.size(), length) == JournalReadResult::Missing && !length);
}
void ActualOwnerSetCommitCutsAndReopen() {
  for (unsigned cut = 0; cut < 3; ++cut) {
    nvs = {};
    EspIdfKeepsakeInventoryStorage storage(true);
    KeepsakeCommittedInventory owner(storage);
    JournalEvidenceInventory inventory;
    assert(owner.ReadCommitted(inventory) && inventory.origin == JournalInventoryOrigin::Missing);
    assert(owner.Commit({}) && !nvs.sets && !nvs.keyExists);
    assert(owner.Establish(Device, true));
    const auto before = nvs.committed;
    if (cut == 0) nvs.failSet = true;
    if (cut == 1) nvs.commitCut = CommitCut::Before;
    if (cut == 2) nvs.commitCut = CommitCut::After;
    assert(!owner.Commit({{Candidate()}, {}, {Candidate()}}));
    assert(!owner.IsHealthy() && !owner.ReadCommitted(inventory));
    assert(inventory.origin == JournalInventoryOrigin::Unavailable);
    assert((nvs.committed == before) == (cut != 2));
    nvs.failSet = false; nvs.commitCut = CommitCut::None;
    assert(!owner.ReadCommitted(inventory)); // Port recovery alone does not unpoison the cache.
    assert(owner.Reopen() && owner.ReadCommitted(inventory));
    assert(inventory.complete && inventory.deviceId == Device && inventory.generation == (cut == 2 ? 2u : 1u));
    assert(inventory.count == (cut == 2 ? 1u : 0u));
    nvs.failRead = true;
    assert(!owner.ReadCommitted(inventory) && !owner.IsHealthy());
    nvs.failRead = false;
    assert(owner.Reopen() && owner.ReadCommitted(inventory));
    assert(nvs.opens == nvs.closes);
  }
}
}

esp_err_t nvs_open(const char* name, int mode, nvs_handle_t* handle) {
  assert(std::strcmp(name, "dv10_outbox") == 0 && (mode == NVS_READONLY || mode == NVS_READWRITE));
  if (nvs.failOpen) return ESP_FAIL;
  if (!nvs.nameExists && mode == NVS_READONLY) return ESP_ERR_NVS_NOT_FOUND;
  nvs.nameExists = true; ++nvs.opens; *handle = 1; return ESP_OK;
}
void nvs_close(nvs_handle_t handle) { assert(handle == 1); ++nvs.closes; }
esp_err_t nvs_get_blob(nvs_handle_t handle, const char* key, void* target, size_t* length) {
  assert(handle == 1 && std::strcmp(key, "completed") == 0);
  if (!nvs.keyExists) return ESP_ERR_NVS_NOT_FOUND;
  if (!target) { ++nvs.sizes; if (nvs.failSize) return ESP_FAIL; *length = nvs.committed.size(); return ESP_OK; }
  ++nvs.reads;
  if (nvs.failRead || *length < nvs.committed.size()) return ESP_FAIL;
  std::copy(nvs.committed.begin(), nvs.committed.end(), static_cast<uint8_t*>(target));
  *length = nvs.committed.size(); return ESP_OK;
}
esp_err_t nvs_set_blob(nvs_handle_t handle, const char* key, const void* source, size_t length) {
  assert(handle == 1 && std::strcmp(key, "completed") == 0);
  ++nvs.sets;
  if (nvs.failSet) return ESP_FAIL;
  const auto* bytes = static_cast<const uint8_t*>(source);
  nvs.staged.assign(bytes, bytes + length); return ESP_OK;
}
esp_err_t nvs_commit(nvs_handle_t handle) {
  assert(handle == 1); ++nvs.commits;
  if (nvs.commitCut == CommitCut::Before) return ESP_FAIL;
  nvs.committed = nvs.staged; nvs.keyExists = true;
  return nvs.commitCut == CommitCut::After ? ESP_FAIL : ESP_OK;
}

int main() {
  MissingBoundsAndReadFailures();
  ActualOwnerSetCommitCutsAndReopen();
  std::cout << "Real online NVS adapter bounds, missing origin, read/set/commit failures and lost ACK reopen passed\n";
}

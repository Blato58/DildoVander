#include <algorithm>
#include <cassert>
#include <cstring>
#include <map>
#include <string>
#include <vector>

#include "../src/features/offline_show/EspIdfOfflineOutboxStorage.h"

namespace {

using namespace kufr::offline;
constexpr char Device[] = "kufr-001";
constexpr char Operation[] = "abcdef01-2345-4678-9abc-def012345678";
constexpr char Boot[] = "12345678-abcd-4abc-8abc-123456789abc";
constexpr char Evening[] = "87654321-abcd-4abc-8abc-123456789abc";

enum class SetCut { None, Before, AfterDraft };
enum class CommitCut { None, Before, After };
struct Handle {
  bool pending = false;
  std::vector<uint8_t> draft;
};
struct State {
  bool present = false, openFailure = false, sizeFailure = false, valueFailure = false, lengthMismatch = false;
  SetCut setCut = SetCut::None;
  CommitCut commitCut = CommitCut::None;
  std::vector<uint8_t> bytes;
  std::map<nvs_handle_t, Handle> handles;
  nvs_handle_t nextHandle = 1;
  size_t opens = 0, closes = 0, sizeReads = 0, valueReads = 0, sets = 0, commits = 0, discardedDrafts = 0;
} state;

void Reset() { assert(state.handles.empty()); state = {}; }

template<class T> void Seed(const T& value) {
  const auto* begin = reinterpret_cast<const uint8_t*>(&value);
  state.bytes.assign(begin, begin + sizeof(value));
  state.present = true;
}

Outbox Record(bool completed = false) {
  Outbox outbox;
  auto& record = outbox.records[0];
  std::copy(Operation, Operation + 37, record.operationId.begin());
  std::copy(Boot, Boot + 37, record.bootId.begin());
  std::copy(Evening, Evening + 37, record.localEveningId.begin());
  record.slot = 2; record.generation = 9; record.startedAtMs = 10;
  record.completed = completed; record.completedAtMs = completed ? 100 : 0;
  record.operationEpoch = 9007199254740993ULL; record.physicalFingerprint = 77; record.deviceRevision = 12;
  return outbox;
}

JournalEvidenceInventory Inventory(const EspIdfOutboxStorage& storage) {
  JournalEvidenceInventory inventory;
  assert(storage.ReadCommitted(inventory));
  return inventory;
}

void AssertUnavailable(EspIdfOutboxStorage& storage) {
  JournalEvidenceInventory inventory;
  inventory.deviceId = "must-clear"; inventory.generation = 7; inventory.complete = true; inventory.count = 1;
  assert(!storage.ReadCommitted(inventory));
  assert(inventory.origin == JournalInventoryOrigin::Unavailable && !inventory.complete
      && inventory.deviceId.empty() && inventory.generation == 0 && inventory.count == 0);
  Outbox outbox;
  assert(!storage.Read(outbox));
}

void SeedBound(bool record = false, bool completed = false) {
  EspIdfOutboxStorage storage(true);
  assert(storage.EstablishBinding(Device, true));
  if (record) assert(storage.Write(Record(completed)));
}

void TestNamespaceMissingAndReopenLifetime() {
  Reset();
  {
    EspIdfOutboxStorage storage(false);
    AssertUnavailable(storage);
    assert(!storage.EstablishBinding(Device, true) && !storage.Reopen());
    assert(state.opens == 0 && state.sizeReads == 0 && state.sets == 0);
  }
  Reset();
  state.openFailure = true;
  {
    EspIdfOutboxStorage storage(true);
    AssertUnavailable(storage);
    assert(state.opens == 1 && state.handles.empty());
    state.openFailure = false;
    assert(storage.Reopen());
    const auto inventory = Inventory(storage);
    assert(inventory.origin == JournalInventoryOrigin::Missing && !inventory.complete && inventory.generation == 0);
    assert(state.opens == 2 && state.handles.size() == 1 && state.valueReads == 0);
    assert(!storage.EstablishBinding(Device, false) && state.sets == 0);
    assert(storage.EstablishBinding(Device, true));
    assert(Inventory(storage).complete && Inventory(storage).count == 0);
    const auto opens = state.opens, closes = state.closes;
    assert(storage.Reopen());
    assert(state.opens == opens + 1 && state.closes == closes + 1 && state.handles.size() == 1);
    assert(Inventory(storage).deviceId == Device && Inventory(storage).generation == 1);
    state.openFailure = true;
    assert(!storage.Reopen()); AssertUnavailable(storage);
    assert(state.handles.empty());
    state.openFailure = false;
    assert(storage.Reopen() && Inventory(storage).generation == 1);
  }
  assert(state.handles.empty());
}

void TestEmptyBoundsAndLegacyBytes() {
  Reset();
  {
    EspIdfOutboxStorage missing(true);
    Outbox outbox;
    assert(missing.Read(outbox) && outbox.version == 2);
    assert(Inventory(missing).origin == JournalInventoryOrigin::Missing && !Inventory(missing).complete);
    assert(state.sets == 0 && state.commits == 0);
    outbox.quiet = true;
    assert(missing.Write(outbox));
    assert(state.bytes.size() == sizeof(Outbox));
    const auto inventory = Inventory(missing);
    assert(inventory.origin == JournalInventoryOrigin::Valid && !inventory.complete && inventory.ambiguous
        && inventory.generation == 0); // Production legacy writes never bind implicitly.
  }
  Reset(); Seed(Outbox{});
  {
    EspIdfOutboxStorage empty(true);
    assert(Inventory(empty).origin == JournalInventoryOrigin::Valid && !Inventory(empty).complete);
    assert(state.valueReads == 1 && state.sets == 0);
  }
  Reset();
  storage::LegacyOutboxV1 legacy; legacy.quiet = true; Seed(legacy);
  {
    EspIdfOutboxStorage old(true); Outbox outbox;
    assert(old.Read(outbox) && outbox.version == 2 && outbox.quiet);
    assert(Inventory(old).origin == JournalInventoryOrigin::Valid && !Inventory(old).complete && state.sets == 0);
  }
  Reset(); state.present = true; // A found zero-byte blob is not Missing or an empty v2 outbox.
  {
    EspIdfOutboxStorage zero(true); AssertUnavailable(zero);
    assert(state.valueReads == 0 && !zero.EstablishBinding(Device, true) && state.sets == 0);
  }
  Reset(); state.present = true;
  state.bytes.assign(OfflineCommittedInventory::MaximumRecordSize + 1, 0);
  {
    EspIdfOutboxStorage oversized(true); AssertUnavailable(oversized);
    assert(state.valueReads == 0 && !oversized.EstablishBinding(Device, true) && state.sets == 0);
  }
  Reset(); SeedBound();
  {
    EspIdfOutboxStorage bound(true);
    assert(state.bytes.size() == OfflineCommittedInventory::MaximumRecordSize);
    assert(Inventory(bound).complete && !Inventory(bound).ambiguous && Inventory(bound).generation == 1);
  }
}

void TestGetFailuresPoisonUntilReopen() {
  for (unsigned fault = 0; fault < 3; ++fault) {
    Reset(); SeedBound(true);
    state.sizeFailure = fault == 0;
    state.valueFailure = fault == 1;
    state.lengthMismatch = fault == 2;
    {
      EspIdfOutboxStorage storage(true); AssertUnavailable(storage);
      const auto sizeReads = state.sizeReads, valueReads = state.valueReads, sets = state.sets;
      state.sizeFailure = false; state.valueFailure = false; state.lengthMismatch = false;
      AssertUnavailable(storage);
      assert(!storage.Write(Record()) && state.sizeReads == sizeReads && state.valueReads == valueReads && state.sets == sets);
      const auto closes = state.closes;
      assert(storage.Reopen() && state.closes == closes + 1);
      const auto inventory = Inventory(storage);
      assert(inventory.complete && inventory.generation == 2 && inventory.count == 1
          && inventory.references[0].operationId == Operation);
    }
    assert(state.handles.empty());
  }
}

void TestSetAndCommitFailuresDiscardOrReloadAuthority() {
  for (unsigned fault = 0; fault < 4; ++fault) {
    Reset(); SeedBound();
    {
      EspIdfOutboxStorage storage(true);
      state.setCut = fault == 0 ? SetCut::Before : fault == 1 ? SetCut::AfterDraft : SetCut::None;
      state.commitCut = fault == 2 ? CommitCut::Before : fault == 3 ? CommitCut::After : CommitCut::None;
      const auto commits = state.commits;
      assert(!storage.Write(Record())); AssertUnavailable(storage);
      assert(state.commits == commits + (fault >= 2 ? 1 : 0));
      const auto sets = state.sets, sizeReads = state.sizeReads, valueReads = state.valueReads;
      state.setCut = SetCut::None; state.commitCut = CommitCut::None;
      assert(!storage.Write(Record()) && !storage.EstablishBinding(Device, true));
      AssertUnavailable(storage);
      assert(state.sets == sets && state.sizeReads == sizeReads && state.valueReads == valueReads);
      const auto closes = state.closes, discarded = state.discardedDrafts;
      assert(storage.Reopen() && state.closes == closes + 1);
      assert(state.discardedDrafts == discarded + (fault == 1 || fault == 2 ? 1 : 0));
      const auto inventory = Inventory(storage);
      assert(inventory.complete && !inventory.ambiguous && inventory.generation == (fault == 3 ? 2 : 1));
      assert(inventory.count == (fault == 3 ? 1 : 0));
      if (inventory.count) assert(inventory.references[0].epoch == 9007199254740993ULL
          && inventory.references[0].fingerprint == 77 && inventory.references[0].operationId == Operation);
    }
    assert(state.handles.empty());
  }
}

void TestLostBindingAndAcknowledgeResponses() {
  for (const auto cut : {CommitCut::Before, CommitCut::After}) {
    Reset();
    {
      EspIdfOutboxStorage storage(true); state.commitCut = cut;
      assert(!storage.EstablishBinding(Device, true)); AssertUnavailable(storage);
      state.commitCut = CommitCut::None;
      assert(storage.Reopen());
      const auto inventory = Inventory(storage);
      if (cut == CommitCut::Before) {
        assert(inventory.origin == JournalInventoryOrigin::Missing && !inventory.complete);
        assert(storage.EstablishBinding(Device, true));
      } else {
        assert(inventory.complete && inventory.generation == 1 && inventory.count == 0);
        const auto sets = state.sets, commits = state.commits;
        assert(storage.EstablishBinding(Device, true) && state.sets == sets && state.commits == commits);
      }
    }
    Reset(); SeedBound(true, true);
    {
      EspIdfOutboxStorage storage(true); state.commitCut = cut;
      assert(!storage.Write(Outbox{})); AssertUnavailable(storage);
      state.commitCut = CommitCut::None;
      assert(storage.Reopen());
      const auto inventory = Inventory(storage);
      assert(inventory.complete && inventory.generation == (cut == CommitCut::Before ? 2 : 3)
          && inventory.count == (cut == CommitCut::Before ? 1 : 0));
      if (inventory.count) assert(inventory.references[0].operationId == Operation);
    }
    assert(state.handles.empty());
  }
}

}  // namespace

esp_err_t nvs_open(const char* name, int mode, nvs_handle_t* handle) {
  assert(std::strcmp(name, "offline_show") == 0 && mode == NVS_READWRITE && handle);
  ++state.opens;
  if (state.openFailure) return ESP_FAIL;
  *handle = state.nextHandle++;
  state.handles.emplace(*handle, Handle{});
  return ESP_OK;
}
void nvs_close(nvs_handle_t handle) {
  auto found = state.handles.find(handle);
  assert(found != state.handles.end());
  if (found->second.pending) ++state.discardedDrafts;
  state.handles.erase(found); ++state.closes;
}
esp_err_t nvs_get_blob(nvs_handle_t handle, const char* key, void* data, size_t* length) {
  assert(std::strcmp(key, "outbox") == 0 && length);
  auto& opened = state.handles.at(handle);
  const auto& visible = opened.pending ? opened.draft : state.bytes;
  if (!data) {
    ++state.sizeReads;
    if (state.sizeFailure) return ESP_FAIL;
    if (!opened.pending && !state.present) return ESP_ERR_NVS_NOT_FOUND;
    *length = visible.size();
    return ESP_OK;
  }
  ++state.valueReads;
  if (state.valueFailure) return ESP_FAIL;
  assert(*length <= OfflineCommittedInventory::MaximumRecordSize);
  if (*length < visible.size()) { *length = visible.size(); return ESP_FAIL; }
  std::copy(visible.begin(), visible.end(), static_cast<uint8_t*>(data));
  *length = visible.size() + (state.lengthMismatch ? 1 : 0);
  return ESP_OK;
}
esp_err_t nvs_set_blob(nvs_handle_t handle, const char* key, const void* data, size_t length) {
  assert(std::strcmp(key, "outbox") == 0 && data && length && length <= OfflineCommittedInventory::MaximumRecordSize);
  auto& opened = state.handles.at(handle);
  ++state.sets;
  if (state.setCut == SetCut::Before) return ESP_FAIL;
  const auto* bytes = static_cast<const uint8_t*>(data);
  opened.draft.assign(bytes, bytes + length); opened.pending = true;
  return state.setCut == SetCut::AfterDraft ? ESP_FAIL : ESP_OK;
}
esp_err_t nvs_commit(nvs_handle_t handle) {
  auto& opened = state.handles.at(handle);
  assert(opened.pending);
  ++state.commits;
  if (state.commitCut == CommitCut::Before) return ESP_FAIL;
  state.bytes = opened.draft; state.present = true;
  opened.draft.clear(); opened.pending = false;
  return state.commitCut == CommitCut::After ? ESP_FAIL : ESP_OK;
}

int main() {
  TestNamespaceMissingAndReopenLifetime();
  TestEmptyBoundsAndLegacyBytes();
  TestGetFailuresPoisonUntilReopen();
  TestSetAndCommitFailuresDiscardOrReloadAuthority();
  TestLostBindingAndAcknowledgeResponses();
  assert(state.handles.empty());
}

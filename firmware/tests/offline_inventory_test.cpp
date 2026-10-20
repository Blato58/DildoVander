#include <algorithm>
#include <array>
#include <cassert>
#include <cstddef>
#include <cstring>
#include <limits>
#include <string>
#include <vector>

#include "../src/features/offline_show/OfflineCommittedInventory.h"

namespace {

using namespace kufr::offline;
using Owner = OfflineCommittedInventory;
constexpr char Device[] = "kufr-001";
constexpr char Operation[] = "abcdef01-2345-4678-9abc-def012345678";
constexpr char Boot[] = "12345678-abcd-4abc-8abc-123456789abc";
constexpr char Evening[] = "87654321-abcd-4abc-8abc-123456789abc";

class Storage final : public JournalStorage {
 public:
  enum class Cut { None, Before, After };
  JournalReadResult Read(uint8_t* output, size_t capacity, size_t& length) override {
    ++reads;
    if (readFailure || bytes.size() > capacity) return JournalReadResult::Failed;
    if (!present) return JournalReadResult::Missing;
    length = bytes.size();
    std::copy(bytes.begin(), bytes.end(), output);
    return JournalReadResult::Found;
  }
  bool Write(const uint8_t* data, size_t length) override {
    ++writes;
    if (cut == Cut::Before) return false;
    // Atomic replacement represents the real storage contract. This committed
    // snapshot, rather than a producer draft, survives every owner restart.
    bytes.assign(data, data + length);
    present = true;
    return cut != Cut::After;
  }
  template<class T> void Seed(const T& value) {
    const auto* begin = reinterpret_cast<const uint8_t*>(&value);
    bytes.assign(begin, begin + sizeof(value));
    present = true;
  }
  std::vector<uint8_t> bytes;
  bool present = false, readFailure = false;
  size_t reads = 0, writes = 0;
  Cut cut = Cut::None;
};

void Copy(std::array<char, 37>& target, const char* value) {
  assert(std::strlen(value) == 36);
  std::copy(value, value + 37, target.begin());
}

Outbox Record(bool completed = false) {
  Outbox outbox;
  auto& record = outbox.records[0];
  Copy(record.operationId, Operation); Copy(record.bootId, Boot); Copy(record.localEveningId, Evening);
  record.generation = 9; record.startedAtMs = 10; record.completedAtMs = completed ? 100 : 0;
  record.slot = 2; record.completed = completed; record.scripted = true; record.shortVariant = true;
  record.operationEpoch = 9007199254740993ULL;
  record.physicalFingerprint = std::numeric_limits<uint64_t>::max();
  record.deviceRevision = 12;
  return outbox;
}

void AssertReference(const JournalEvidenceInventory& inventory, const Outcome& record) {
  assert(inventory.count == 1);
  const auto& identity = inventory.references[0];
  assert(identity.epoch == record.operationEpoch && identity.operationId == record.operationId.data()
      && identity.compartment == record.slot && identity.fingerprint == record.physicalFingerprint);
}

void AssertUnavailable(Owner& owner) {
  JournalEvidenceInventory inventory;
  inventory.deviceId = "must-clear"; inventory.generation = 7; inventory.complete = true; inventory.count = 1;
  assert(!owner.ReadCommitted(inventory));
  assert(inventory.origin == JournalInventoryOrigin::Unavailable && !inventory.complete
      && inventory.deviceId.empty() && inventory.generation == 0 && inventory.count == 0);
  Outbox unchanged = Record();
  assert(!owner.Read(unchanged) && unchanged.records[0].operationId[0]);
}

void TestMissingEmptyAndExplicitBinding() {
  Storage storage;
  Owner owner(storage);
  assert(owner.IsHealthy() && !owner.IsBound() && owner.Generation() == 0);
  Outbox outbox;
  assert(owner.Read(outbox) && outbox.version == 2);
  JournalEvidenceInventory inventory;
  assert(owner.ReadCommitted(inventory) && inventory.origin == JournalInventoryOrigin::Missing
      && !inventory.complete && inventory.count == 0 && inventory.generation == 0);
  assert(!owner.EstablishBinding(Device, false) && storage.writes == 0);
  for (const auto& invalid : std::vector<std::string>{"", "bad device", "../device", std::string(65, 'a')}) {
    assert(!owner.EstablishBinding(invalid, true));
  }
  assert(storage.writes == 0 && owner.EstablishBinding(Device, true));
  assert(owner.IsBound() && owner.Generation() == 1 && storage.bytes.size() == Owner::EnvelopeSize);
  assert(owner.ReadCommitted(inventory) && inventory.origin == JournalInventoryOrigin::Valid
      && inventory.deviceId == Device && inventory.complete && !inventory.ambiguous && inventory.count == 0);
  const auto writes = storage.writes;
  assert(owner.EstablishBinding(Device, true) && storage.writes == writes);
  assert(!owner.EstablishBinding("other-device", true) && owner.BoundDeviceId() == Device);
  outbox.quiet = true;
  assert(owner.Write(outbox) && owner.Generation() == 2);
  Owner reopened(storage);
  assert(reopened.IsBound() && reopened.Generation() == 2 && reopened.Read(outbox) && outbox.quiet);

  Storage emptyBlob;
  emptyBlob.present = true;
  Owner malformed(emptyBlob);
  assert(!malformed.IsHealthy() && !malformed.EstablishBinding(Device, true));
  AssertUnavailable(malformed);
  Storage rawEmpty;
  rawEmpty.Seed(Outbox{});
  Owner legacyEmpty(rawEmpty);
  assert(legacyEmpty.ReadCommitted(inventory) && inventory.origin == JournalInventoryOrigin::Valid
      && !inventory.complete && inventory.ambiguous && inventory.generation == 0);
  assert(!legacyEmpty.IsBound() && rawEmpty.writes == 0);
}

void TestLegacyV1V2BytesRemainUnbound() {
  storage::LegacyOutboxV1 legacy;
  legacy.offlineEnabled = true; legacy.ambientEnabled = true; legacy.quiet = true;
  legacy.serverSettingsRevision = 17; legacy.serverStopRevision = 19;
  auto& record = legacy.records[0];
  Copy(record.operationId, Operation); Copy(record.bootId, Boot); Copy(record.localEveningId, Evening);
  record.generation = 9; record.startedAtMs = 10; record.completedAtMs = 100; record.slot = 2;
  record.completed = true; record.scripted = true; record.shortVariant = true;
  Storage bytes;
  bytes.Seed(legacy);
  const auto original = bytes.bytes;
  Owner owner(bytes);
  Outbox outbox;
  assert(owner.Read(outbox) && outbox.version == 2 && !owner.IsBound());
  assert(outbox.records[0].operationId == record.operationId && outbox.records[0].completed
      && outbox.records[0].operationEpoch == 0 && outbox.records[0].physicalFingerprint == 0);
  assert(outbox.serverSettingsRevision == 17 && outbox.serverStopRevision == 19);
  assert(bytes.bytes == original && bytes.writes == 0);
  JournalEvidenceInventory inventory;
  assert(owner.ReadCommitted(inventory) && !inventory.complete && inventory.ambiguous && inventory.generation == 0);
  AssertReference(inventory, outbox.records[0]);
  assert(owner.Write(outbox) && bytes.bytes.size() == sizeof(Outbox) && owner.Generation() == 0);
  Outbox decoded;
  assert(storage::DecodeOutboxBytes(bytes.bytes.data(), bytes.bytes.size(), decoded));
  assert(decoded.records[0].operationId == record.operationId && decoded.records[0].physicalFingerprint == 0);
  Owner v2(bytes);
  assert(!v2.IsBound() && v2.ReadCommitted(inventory) && inventory.ambiguous);
  assert(v2.EstablishBinding(Device, true) && v2.ReadCommitted(inventory));
  assert(inventory.complete && inventory.ambiguous && inventory.generation == 1);
  assert(v2.Read(decoded) && decoded.records[0].operationEpoch == 0 && decoded.records[0].physicalFingerprint == 0);
  for (const auto cut : {Storage::Cut::Before, Storage::Cut::After}) {
    Storage legacyBytes; legacyBytes.Seed(legacy);
    Owner migrating(legacyBytes); legacyBytes.cut = cut;
    assert(!migrating.EstablishBinding(Device, true)); AssertUnavailable(migrating);
    legacyBytes.cut = Storage::Cut::None;
    assert(migrating.Reopen() && migrating.Read(decoded) && migrating.ReadCommitted(inventory));
    assert(migrating.IsBound() == (cut == Storage::Cut::After));
    assert(inventory.ambiguous && decoded.records[0].operationId == legacy.records[0].operationId
        && decoded.records[0].operationEpoch == 0 && decoded.records[0].physicalFingerprint == 0);
  }
}

void TestCanonicalEnvelopeAndEveryLiveRecord() {
  Storage storage;
  Owner owner(storage);
  assert(owner.EstablishBinding(std::string(64, '_'), true));
  Outbox outbox = Record();
  outbox.serverSettingsRevision = std::numeric_limits<uint64_t>::max();
  outbox.serverStopRevision = std::numeric_limits<uint64_t>::max();
  outbox.records[1] = outbox.records[0];
  Copy(outbox.records[1].operationId, "00000000-abcd-4abc-8abc-000000000002");
  outbox.records[1].completed = true; outbox.records[1].completedAtMs = 100;
  outbox.records[2] = outbox.records[0];
  Copy(outbox.records[2].operationId, "00000000-abcd-4abc-8abc-000000000003");
  outbox.records[2].operationEpoch = std::numeric_limits<uint64_t>::max();
  assert(owner.Write(outbox));
  assert(storage.bytes.size() == Owner::EnvelopeSize && storage.bytes[8] == 2);
  for (size_t byte = 9; byte < 16; ++byte) assert(storage.bytes[byte] == 0);
  Owner reopened(storage);
  Outbox read;
  assert(reopened.Read(read) && read.serverStopRevision == outbox.serverStopRevision
      && read.serverSettingsRevision == outbox.serverSettingsRevision);
  assert(read.records[2].operationEpoch == std::numeric_limits<uint64_t>::max()
      && read.records[2].physicalFingerprint == std::numeric_limits<uint64_t>::max());
  JournalEvidenceInventory inventory;
  assert(reopened.ReadCommitted(inventory) && inventory.complete && !inventory.ambiguous && inventory.count == 3);
  for (size_t index = 0; index < 3; ++index) {
    assert(inventory.references[index].operationId == outbox.records[index].operationId.data());
    assert(inventory.references[index].epoch == outbox.records[index].operationEpoch);
  }
  const auto before = storage.bytes;
  Outbox draft = read; draft.records[0] = {};
  assert(reopened.ReadCommitted(inventory) && inventory.count == 3 && storage.bytes == before);
  assert(reopened.Write(draft) && reopened.Generation() == 3);
  assert(reopened.ReadCommitted(inventory) && inventory.count == 2);
}

uint32_t Crc32(const std::vector<uint8_t>& bytes) {
  uint32_t crc = 0xffffffffU;
  for (size_t index = 0; index < bytes.size() - 4; ++index) {
    crc ^= bytes[index];
    for (unsigned bit = 0; bit < 8; ++bit) crc = (crc >> 1) ^ ((crc & 1) ? 0xedb88320U : 0);
  }
  return ~crc;
}
void RefreshCrc(Storage& storage) {
  const auto crc = Crc32(storage.bytes);
  for (unsigned byte = 0; byte < 4; ++byte) storage.bytes[storage.bytes.size() - 4 + byte] = static_cast<uint8_t>(crc >> (8 * byte));
}

void TestIntegrityMalformedRecordsAndAmbiguity() {
  Storage storage;
  Owner valid(storage);
  assert(valid.EstablishBinding(Device, true) && valid.Write(Record()));
  const auto original = storage.bytes;
  for (size_t byte = 0; byte < original.size(); ++byte) {
    storage.bytes = original; storage.bytes[byte] ^= 1;
    Owner corrupt(storage);
    assert(!corrupt.IsHealthy()); AssertUnavailable(corrupt);
  }
  for (const auto length : std::array<size_t, 3>{{3, Owner::EnvelopeSize - 1, Owner::EnvelopeSize + 1}}) {
    storage.bytes = original; storage.bytes.resize(length);
    Owner truncatedOrOversized(storage);
    assert(!truncatedOrOversized.IsHealthy()); AssertUnavailable(truncatedOrOversized);
  }
  const std::array<size_t, 8> invalidOffsets{{4, 5, 16, 81, 85, 104, 104 + 36, 104 + 135}};
  for (const auto offset : invalidOffsets) {
    storage.bytes = original;
    storage.bytes[offset] = offset == 16 ? 65 : offset == 104 + 135 ? 7 : 0xff;
    RefreshCrc(storage);
    Owner malformed(storage);
    assert(!malformed.IsHealthy()); AssertUnavailable(malformed);
  }
  storage.bytes = original;
  storage.bytes[17 + std::strlen(Device)] = 'x'; // Noncanonical device padding.
  RefreshCrc(storage);
  Owner padding(storage); assert(!padding.IsHealthy());
  storage.bytes = original;
  for (size_t byte = 8; byte < 16; ++byte) storage.bytes[byte] = 0;
  RefreshCrc(storage);
  Owner zeroGeneration(storage); assert(!zeroGeneration.IsHealthy());

  storage.bytes = original;
  Owner owner(storage);
  auto malformedAssociation = Record();
  malformedAssociation.records[0].physicalFingerprint = 0;
  assert(owner.Write(malformedAssociation));
  JournalEvidenceInventory inventory;
  assert(owner.ReadCommitted(inventory) && inventory.complete && inventory.ambiguous && inventory.count == 1);
  auto duplicate = Record(); duplicate.records[1] = duplicate.records[0];
  assert(owner.Write(duplicate) && owner.ReadCommitted(inventory) && inventory.ambiguous && inventory.count == 2);
  Outbox hidden;
  hidden.records[0].slot = 2; // Unknown old bytes without an operation UUID are not proof of empty.
  assert(owner.Write(hidden) && owner.ReadCommitted(inventory) && inventory.ambiguous && inventory.count == 0);

  Storage legacyBad;
  legacyBad.Seed(Outbox{});
  legacyBad.bytes[offsetof(Outbox, quiet)] = 2;
  Owner invalidBool(legacyBad); assert(!invalidBool.IsHealthy());
}

void TestBindingCutsAndAuthoritativeReopen() {
  for (const auto cut : {Storage::Cut::Before, Storage::Cut::After}) {
    Storage storage; Owner owner(storage);
    storage.cut = cut;
    assert(!owner.EstablishBinding(Device, true));
    AssertUnavailable(owner);
    const auto writes = storage.writes;
    assert(!owner.Write(Record()) && storage.writes == writes && owner.Generation() == 0);
    storage.cut = Storage::Cut::None;
    assert(owner.Reopen());
    if (cut == Storage::Cut::Before) {
      assert(!owner.IsBound() && owner.Origin() == JournalInventoryOrigin::Missing);
      assert(owner.EstablishBinding(Device, true));
    } else {
      assert(owner.IsBound() && owner.Generation() == 1);
      assert(owner.EstablishBinding(Device, true) && storage.writes == writes);
    }
    JournalEvidenceInventory inventory;
    assert(owner.ReadCommitted(inventory) && inventory.complete && !inventory.ambiguous && inventory.count == 0);
  }
}

void TestReserveCompleteAcknowledgeCuts() {
  for (unsigned stage = 0; stage < 3; ++stage) {
    for (const auto cut : {Storage::Cut::Before, Storage::Cut::After}) {
      Storage storage;
      {
        Owner setup(storage);
        assert(setup.EstablishBinding(Device, true));
        if (stage >= 1) assert(setup.Write(Record()));
        if (stage >= 2) assert(setup.Write(Record(true)));
      }
      Owner owner(storage);
      const auto oldGeneration = owner.Generation();
      const Outbox next = stage == 0 ? Record() : stage == 1 ? Record(true) : Outbox{};
      storage.cut = cut;
      assert(!owner.Write(next));
      AssertUnavailable(owner);
      const auto writes = storage.writes;
      assert(!owner.Write(next) && storage.writes == writes);
      storage.cut = Storage::Cut::None;
      Owner reopened(storage);
      JournalEvidenceInventory inventory;
      Outbox actual;
      assert(reopened.Read(actual) && reopened.ReadCommitted(inventory));
      assert(reopened.Generation() == oldGeneration + (cut == Storage::Cut::After ? 1 : 0));
      const bool committed = cut == Storage::Cut::After;
      const size_t expectedCount = stage == 0 ? (committed ? 1 : 0) : stage == 1 ? 1 : (committed ? 0 : 1);
      assert(inventory.count == expectedCount && inventory.complete && !inventory.ambiguous);
      if (inventory.count) {
        AssertReference(inventory, actual.records[0]);
        assert(actual.records[0].completed == (stage == 1 ? committed : stage == 2));
      }
      // Failed ACK never erases a reference in the old committed snapshot;
      // lost ACK after commit is resolved only by reading authoritative bytes.
      if (stage == 2 && !committed) assert(actual.records[0].completed && inventory.count == 1);
    }
  }
}

void TestGenerationExhaustionAndReadFailures() {
  Storage storage;
  {
    Owner setup(storage); assert(setup.EstablishBinding(Device, true) && setup.Write(Record()));
  }
  for (size_t byte = 8; byte < 16; ++byte) storage.bytes[byte] = 0xff;
  RefreshCrc(storage);
  Owner maximum(storage);
  assert(maximum.Generation() == std::numeric_limits<uint64_t>::max());
  const auto original = storage.bytes;
  const auto writes = storage.writes;
  assert(!maximum.Write(Outbox{}) && storage.writes == writes && storage.bytes == original);
  AssertUnavailable(maximum);
  assert(maximum.Reopen() && maximum.Generation() == std::numeric_limits<uint64_t>::max());
  JournalEvidenceInventory inventory;
  assert(maximum.ReadCommitted(inventory) && inventory.generation == std::numeric_limits<uint64_t>::max());
  storage.readFailure = true;
  assert(!maximum.Reopen()); AssertUnavailable(maximum);
  assert(!maximum.EstablishBinding(Device, true) && !maximum.Write(Record()) && storage.writes == writes);
  storage.readFailure = false;
  assert(maximum.Reopen() && maximum.ReadCommitted(inventory) && inventory.count == 1);
}

}  // namespace

int main() {
  TestMissingEmptyAndExplicitBinding();
  TestLegacyV1V2BytesRemainUnbound();
  TestCanonicalEnvelopeAndEveryLiveRecord();
  TestIntegrityMalformedRecordsAndAmbiguity();
  TestBindingCutsAndAuthoritativeReopen();
  TestReserveCompleteAcknowledgeCuts();
  TestGenerationExhaustionAndReadFailures();
}

#include <assert.h>
#include <stdint.h>

#include <algorithm>
#include <array>
#include <functional>
#include <limits>
#include <string>

#include "../src/features/device_control/PersistentCommandJournal.h"

namespace {

using Journal = PersistentCommandJournal;
using Lookup = CommandJournal::Lookup;
constexpr char FirstId[] = "523ef590-ce4d-4901-91b3-79de1bfe7e35";
constexpr char UpperId[] = "523EF590-CE4D-4901-91B3-79DE1BFE7E35";
constexpr char SecondId[] = "7a2fef87-0a80-4115-86f3-3a93ad984b82";
constexpr size_t LegacySize = 44 + Journal::HistoryCapacity * 45;
constexpr size_t EpochOffset = 44;
constexpr size_t EntryOffset = 118;
constexpr size_t EntrySize = 54;

// These cuts model the storage contract: atomic before/after replacement, with
// lost acknowledgement in either case. They do not simulate ESP-IDF/NVS itself.
class Storage final : public JournalStorage {
 public:
  enum class Cut { None, Before, After };
  JournalReadResult Read(uint8_t* output, size_t capacity, size_t& length) override {
    if (failRead || (present && size > capacity)) return JournalReadResult::Failed;
    if (!present) return JournalReadResult::Missing;
    length = size;
    std::copy(bytes.begin(), bytes.begin() + size, output);
    return JournalReadResult::Found;
  }
  bool Write(const uint8_t* data, size_t length) override {
    ++writes;
    if (cut == Cut::Before) return false;
    assert(length <= bytes.size());
    bytes.fill(0);
    std::copy(data, data + length, bytes.begin());
    present = true;
    size = length;
    return cut != Cut::After;
  }
  std::array<uint8_t, Journal::RecordSize> bytes{};
  size_t size = 0;
  size_t writes = 0;
  bool present = false;
  bool failRead = false;
  Cut cut = Cut::None;
};

void Write64(uint8_t* bytes, uint64_t value) {
  for (unsigned index = 0; index < 8; ++index) bytes[index] = static_cast<uint8_t>(value >> (8 * index));
}

void RefreshCrc(Storage& storage) {
  uint32_t crc = 0xffffffff;
  for (size_t index = 0; index < Journal::RecordSize - 4; ++index) {
    crc ^= storage.bytes[index];
    for (unsigned bit = 0; bit < 8; ++bit)
      crc = (crc >> 1) ^ ((crc & 1) ? 0xedb88320 : 0);
  }
  crc = ~crc;
  for (unsigned byte = 0; byte < 4; ++byte)
    storage.bytes[Journal::RecordSize - 4 + byte] = static_cast<uint8_t>(crc >> (8 * byte));
}

Storage Legacy(uint8_t version, bool pending) {
  Storage storage;
  storage.present = true;
  storage.size = version == 1 ? 44 : LegacySize;
  storage.bytes[0] = 'K'; storage.bytes[1] = 'J'; storage.bytes[2] = 'R'; storage.bytes[3] = '1';
  storage.bytes[4] = version;
  storage.bytes[5] = 63;
  if (pending) {
    storage.bytes[6] = 2;
    std::copy(SecondId, SecondId + 36, storage.bytes.begin() + 8);
  }
  if (version != 1) {
    storage.bytes[7] = pending ? 2 : 1;
    std::copy(FirstId, FirstId + 36, storage.bytes.begin() + 44);
    storage.bytes[44 + 36] = static_cast<uint8_t>(1 | (version == 3 ? 0x80 : 0));
    Write64(storage.bytes.data() + 44 + 37, 101);
    if (pending) {
      std::copy(SecondId, SecondId + 36, storage.bytes.begin() + 89);
      storage.bytes[89 + 36] = 2;
      Write64(storage.bytes.data() + 89 + 37, 202);
    }
  }
  return storage;
}

Storage Provisioned() {
  Storage storage;
  Journal journal(storage);
  assert(journal.PersistReadyMask(63));
  assert(journal.EstablishScopedIdentity("kufr-001", true));
  return storage;
}

Storage Pending() {
  Storage storage = Provisioned();
  Journal journal(storage);
  assert(journal.BeginScoped(1, FirstId, 1, 101));
  return storage;
}

Storage Completed(bool timed = false) {
  Storage storage = Pending();
  Journal journal(storage);
  assert(journal.ResolveRestored(1));
  if (timed) assert(journal.ConfirmTimedPresentationScoped(1, FirstId, 1, 101));
  return storage;
}

std::string Id(unsigned number) {
  std::string id = "00000000-0000-0000-0000-000000000000";
  constexpr char hex[] = "0123456789abcdef";
  for (unsigned digit = 0; digit < 8; ++digit) {
    id[35 - digit] = hex[number & 15];
    number >>= 4;
  }
  return id;
}

void TestMissingStorageAndExplicitProvisioning() {
  Storage storage;
  Journal empty(storage);
  assert(empty.IsHealthy() && empty.Origin() == Journal::StateOrigin::Missing);
  assert(empty.IsStorageMissing());
  assert(empty.CurrentEpoch() == 0);
  assert(!empty.EstablishScopedIdentity("kufr-001"));
  assert(!empty.AdvanceEpochAndCompact());
  assert(!empty.BeginScoped(1, FirstId, 1, 101));
  assert(empty.FindScoped(0, FirstId, 1, 101) == Lookup::StaleEpoch);
  assert(empty.FindScoped(1, FirstId, 1, 101) == Lookup::StaleEpoch);
  assert(empty.PersistReadyMask(63));
  assert(!empty.IsStorageMissing());
  // A normal inventory write must not disguise missing identity after reboot.
  Journal stillMissing(storage);
  assert(stillMissing.Origin() == Journal::StateOrigin::Missing);
  assert(!stillMissing.IsStorageMissing());
  assert(!stillMissing.EstablishScopedIdentity("kufr-001"));
  assert(!stillMissing.EstablishScopedIdentity("", true));
  assert(!stillMissing.EstablishScopedIdentity("has space", true));
  assert(!stillMissing.EstablishScopedIdentity(std::string(65, 'a'), true));
  assert(stillMissing.EstablishScopedIdentity("kufr-001", true));
  Journal established(storage);
  assert(established.CurrentEpoch() == 1);
  assert(established.ScopedDeviceId() == "kufr-001");
  assert(established.Origin() == Journal::StateOrigin::Scoped);
  assert(!established.EstablishScopedIdentity("kufr-002", true));
  assert(!established.BeginWithIdentity(FirstId, 1, 101));
  assert(!established.Begin(FirstId, 1));
  assert(established.Find(FirstId, 1, 101) == Lookup::StaleEpoch);
  assert(!established.BeginScoped(0, FirstId, 1, 101));
  assert(!established.BeginScoped(2, FirstId, 1, 101));
  assert(!established.BeginScoped(1, FirstId, 1, 0));
  assert(!established.BeginScoped(1, "bad-id", 1, 101));
  assert(!established.BeginScoped(1, FirstId, 0, 101));
  assert(!established.BeginScoped(1, FirstId, 7, 101));
}

void TestScopedDuplicatesConflictsAndPinOrdering() {
  Storage storage = Provisioned();
  Journal journal(storage);
  assert(journal.BeginScoped(1, FirstId, 1, 101));
  assert(journal.FindScoped(1, UpperId, 1, 101) == Lookup::Same);
  assert(journal.FindScoped(1, UpperId, 2, 101) == Lookup::Conflict);
  assert(journal.FindScoped(1, UpperId, 1, 102) == Lookup::Conflict);
  assert(!journal.BeginScoped(1, UpperId, 1, 101));
  assert(journal.IsEvidencePinned(1, UpperId, 1, 101));
  assert(!journal.ConfirmTimedPresentationScoped(1, FirstId, 1, 101));
  assert(!journal.ReleaseEvidencePin(1, FirstId, 1, 101));
  assert(!journal.AdvanceEpochAndCompact());
  assert(!journal.PersistReadyMask(63));
  assert(!journal.ResolveRestored(2));
  assert(journal.ResolveRestored(1));
  assert(!journal.ConfirmTimedPresentationScoped(0, FirstId, 1, 101));
  assert(!journal.ConfirmTimedPresentationScoped(2, FirstId, 1, 101));
  assert(!journal.ConfirmTimedPresentationScoped(1, FirstId, 2, 101));
  assert(!journal.ConfirmTimedPresentationScoped(1, FirstId, 1, 102));
  assert(journal.ConfirmTimedPresentationScoped(1, UpperId, 1, 101));
  assert(journal.WasTimedPresentationCompletedScoped(1, UpperId, 1, 101));
  assert(!journal.WasTimedPresentationCompletedScoped(1, UpperId, 1, 102));
  assert(journal.AdvanceEpochAndCompact());
  Journal advanced(storage);
  assert(advanced.CurrentEpoch() == 2 && advanced.HistoryCount() == 1);
  assert(advanced.FindScoped(1, FirstId, 1, 101) == Lookup::StaleEpoch);
  assert(advanced.FindHistorical(1, FirstId, 1, 101) == Lookup::Same);
  assert(advanced.WasTimedPresentationCompletedScoped(1, FirstId, 1, 101));
  assert(!advanced.BeginScoped(1, FirstId, 1, 101));
  assert(!advanced.ConfirmTimedPresentationScoped(1, FirstId, 1, 101));
  assert(!advanced.ReleaseEvidencePin(1, FirstId, 2, 101));
  assert(!advanced.ReleaseEvidencePin(2, FirstId, 1, 101));
  assert(!advanced.ReleaseEvidencePin(1, FirstId, 1, 102));
  assert(advanced.ReleaseEvidencePin(1, UpperId, 1, 101));
  const size_t writes = storage.writes;
  assert(advanced.ReleaseEvidencePin(1, FirstId, 1, 101));
  assert(storage.writes == writes);
  assert(advanced.FindHistorical(1, FirstId, 1, 101) == Lookup::Same);
  assert(advanced.AdvanceEpochAndCompact());
  Journal compacted(storage);
  assert(compacted.HistoryCount() == 0);
  assert(compacted.FindHistorical(1, FirstId, 1, 101) == Lookup::Missing);
  assert(compacted.FindScoped(1, FirstId, 1, 101) == Lookup::StaleEpoch);
  assert(!compacted.BeginScoped(1, FirstId, 1, 101));
  // The UUID may be reused only by a newly created envelope in this epoch.
  assert(compacted.BeginScoped(3, FirstId, 2, 202));
  assert(compacted.FindScoped(3, FirstId, 2, 202) == Lookup::Same);
  assert(compacted.ResolveRestored(2));
  assert(compacted.ReleaseEvidencePin(3, FirstId, 2, 202));
  assert(!compacted.ConfirmTimedPresentationScoped(3, FirstId, 2, 202));
}

void TestLegacyMigrationAndEvidence() {
  for (uint8_t version = 1; version <= 3; ++version) {
    for (bool pending : {false, true}) {
      Storage storage = Legacy(version, pending);
      Journal legacy(storage);
      assert(legacy.IsHealthy() && legacy.Origin() == Journal::StateOrigin::Legacy);
      assert(legacy.CurrentEpoch() == 0);
      if (pending) {
        assert(legacy.HasPending());
        assert(!legacy.EstablishScopedIdentity("kufr-001"));
        assert(!legacy.AdvanceEpochAndCompact());
        assert(legacy.Resolve(61));
      }
      assert(legacy.EstablishScopedIdentity("kufr-001"));
      assert(storage.size == Journal::RecordSize && storage.bytes[4] == 4);
      Journal adopted(storage);
      assert(adopted.CurrentEpoch() == 1 && !adopted.HasPending());
      if (pending) {
        const uint64_t fingerprint = version == 1 ? 0 : 202;
        assert(adopted.FindHistorical(0, SecondId, 2, fingerprint) == Lookup::Same);
        assert(adopted.IsEvidencePinned(0, SecondId, 2, fingerprint));
      }
      if (version != 1) {
        assert(adopted.FindHistorical(0, FirstId, 1, 101) == Lookup::Same);
        assert(adopted.IsEvidencePinned(0, FirstId, 1, 101));
        assert(adopted.WasTimedPresentationCompletedHistorical(0, FirstId, 1, 101) == (version == 3));
        assert(adopted.WasLegacyTimedPresentationCompleted(UpperId, 1) == (version == 3));
        assert(!adopted.WasLegacyTimedPresentationCompleted(FirstId, 2));
        assert(!adopted.WasTimedPresentationCompleted(FirstId, 1));
      }
      assert(adopted.FindScoped(0, FirstId, 1, 101) == Lookup::StaleEpoch);
      assert(!adopted.Begin(FirstId, 1));
      const size_t history = adopted.HistoryCount();
      assert(adopted.AdvanceEpochAndCompact());
      Journal compacted(storage);
      assert(compacted.HistoryCount() == history);
      if (version != 1) {
        assert(compacted.WasTimedPresentationCompletedHistorical(0, FirstId, 1, 101) == (version == 3));
        assert(compacted.ReleaseEvidencePin(0, FirstId, 1, 101));
        assert(compacted.AdvanceEpochAndCompact());
        assert(compacted.FindHistorical(0, FirstId, 1, 101) == Lookup::Missing);
      }
    }
  }
}

void TestRetainedUuidIsScopedByEpochAndDeviceBinding() {
  Storage storage = Completed(true);
  Journal first(storage);
  assert(first.AdvanceEpochAndCompact());
  assert(first.BeginScoped(2, UpperId, 2, 202));
  assert(first.ResolveRestored(2));
  assert(first.ConfirmTimedPresentationScoped(2, UpperId, 2, 202));
  Journal sameUuid(storage);
  assert(sameUuid.HistoryCount() == 2);
  assert(sameUuid.FindHistorical(1, UpperId, 1, 101) == Lookup::Same);
  assert(sameUuid.FindHistorical(2, FirstId, 2, 202) == Lookup::Same);
  assert(sameUuid.FindScoped(2, FirstId, 1, 101) == Lookup::Conflict);
  assert(sameUuid.FindScoped(1, FirstId, 1, 101) == Lookup::StaleEpoch);
  assert(sameUuid.AdvanceEpochAndCompact());
  Journal retained(storage);
  assert(retained.WasTimedPresentationCompletedScoped(1, FirstId, 1, 101));
  assert(retained.WasTimedPresentationCompletedScoped(2, UpperId, 2, 202));
  assert(retained.ReleaseEvidencePin(1, FirstId, 1, 101));
  assert(retained.AdvanceEpochAndCompact());
  assert(!retained.WasTimedPresentationCompletedScoped(1, FirstId, 1, 101));
  assert(retained.WasTimedPresentationCompletedScoped(2, UpperId, 2, 202));

  // Storage belongs to one provisioned device namespace. A second device has
  // independent epoch/UUID identity; integration must verify this binding.
  Storage otherStorage;
  Journal other(otherStorage);
  assert(other.EstablishScopedIdentity("other-device", true));
  assert(other.PersistReadyMask(63));
  assert(other.BeginScoped(1, FirstId, 3, 303));
  assert(other.ScopedDeviceId() != retained.ScopedDeviceId());
  assert(other.FindScoped(1, FirstId, 3, 303) == Lookup::Same);
  assert(retained.FindHistorical(1, FirstId, 3, 303) == Lookup::Missing);
}

void TestAllPinnedPressureAndEpochOverflow() {
  Storage storage = Provisioned();
  Journal journal(storage);
  for (unsigned index = 0; index < Journal::HistoryCapacity; ++index) {
    assert(journal.BeginScoped(1, Id(index), 1, index + 1));
    assert(journal.ResolveRestored(1));
  }
  assert(!journal.CanBeginOperation());
  const size_t writes = storage.writes;
  assert(!journal.BeginScoped(1, SecondId, 1, 123));
  assert(!journal.AdvanceEpochAndCompact());
  assert(storage.writes == writes && journal.CurrentEpoch() == 1);
  assert(journal.ReleaseEvidencePin(1, Id(0), 1, 1));
  // Pin release alone must not evict a current-epoch dedupe tombstone.
  assert(!journal.BeginScoped(1, Id(0), 1, 1));
  assert(!journal.CanBeginOperation());
  assert(journal.AdvanceEpochAndCompact());
  assert(journal.CurrentEpoch() == 2 && journal.HistoryCount() == 31);
  assert(journal.BeginScoped(2, Id(0), 1, 1));
  assert(journal.ResolveRestored(1));
  assert(!journal.AdvanceEpochAndCompact());
  // Synthesized valid max-epoch fixture verifies overflow without 2^64 writes.
  Storage maximum = Provisioned();
  Write64(maximum.bytes.data() + EpochOffset, std::numeric_limits<uint64_t>::max());
  RefreshCrc(maximum);
  Journal overflow(maximum);
  assert(overflow.IsHealthy());
  assert(overflow.CurrentEpoch() == std::numeric_limits<uint64_t>::max());
  const size_t maxWrites = maximum.writes;
  assert(!overflow.AdvanceEpochAndCompact());
  assert(maximum.writes == maxWrites);
}

void TestMoreThanThousandOperationsAcrossCompactionsAndReboots() {
  Storage storage = Provisioned();
  {
    Journal journal(storage);
    assert(journal.BeginScoped(1, FirstId, 1, 101));
    assert(journal.ResolveRestored(1));
    assert(journal.ConfirmTimedPresentationScoped(1, FirstId, 1, 101));
  }
  unsigned completed = 0;
  uint64_t epoch = 1;
  for (unsigned round = 0; round < 36; ++round) {
    for (unsigned index = 0; index < 31; ++index) {
      const std::string id = Id(index);
      const uint64_t fingerprint = 1000 + index;
      {
        Journal journal(storage);
        assert(journal.CurrentEpoch() == epoch);
        assert(journal.BeginScoped(epoch, id, 1, fingerprint));
      }
      {
        Journal restartedPending(storage);
        assert(restartedPending.HasPending());
        assert(!restartedPending.AdvanceEpochAndCompact());
        assert(restartedPending.FindScoped(epoch, id, 1, fingerprint) == Lookup::Same);
        assert(restartedPending.ResolveRestored(1));
      }
      {
        Journal restartedComplete(storage);
        assert(restartedComplete.ConfirmTimedPresentationScoped(epoch, id, 1, fingerprint));
      }
      {
        Journal restartedEvidence(storage);
        assert(restartedEvidence.WasTimedPresentationCompletedScoped(epoch, id, 1, fingerprint));
        assert(restartedEvidence.ReleaseEvidencePin(epoch, id, 1, fingerprint));
      }
      Journal reconciled(storage);
      assert(!reconciled.IsEvidencePinned(epoch, id, 1, fingerprint));
      assert(reconciled.FindScoped(epoch, id, 1, fingerprint) == Lookup::Same);
      ++completed;
    }
    Journal full(storage);
    assert(full.HistoryCount() == Journal::HistoryCapacity);
    assert(full.AdvanceEpochAndCompact());
    ++epoch;
    Journal compacted(storage);
    assert(compacted.HistoryCount() == 1 && compacted.CurrentEpoch() == epoch);
    assert(compacted.WasTimedPresentationCompletedScoped(1, FirstId, 1, 101));
    assert(compacted.IsEvidencePinned(1, FirstId, 1, 101));
    assert(compacted.FindScoped(epoch - 1, Id(0), 1, 1000) == Lookup::StaleEpoch);
    assert(!compacted.BeginScoped(epoch - 1, Id(0), 1, 1000));
    assert(compacted.FindHistorical(epoch - 1, Id(0), 1, 1000) == Lookup::Missing);
  }
  assert(completed == 1116);
}

using Mutation = std::function<bool(Journal&)>;
using Verify = std::function<void(const Journal&, bool)>;

void CheckCuts(const Storage& baseline, const Mutation& mutate, const Verify& verify) {
  for (Storage::Cut cut : {Storage::Cut::Before, Storage::Cut::After}) {
    Storage storage = baseline;
    storage.cut = cut;
    Journal journal(storage);
    assert(journal.IsHealthy());
    assert(!mutate(journal));
    assert(!journal.IsHealthy() && journal.HasPending());
    assert(journal.ReadyMask() == 0 && journal.CurrentEpoch() == 0);
    assert(journal.FindScoped(1, FirstId, 1, 101) == Lookup::Unavailable);
    assert(!journal.BeginScoped(1, SecondId, 1, 202));
    assert(!journal.EstablishScopedIdentity("kufr-001", true));
    assert(!journal.AdvanceEpochAndCompact());
    storage.cut = Storage::Cut::None;
    Journal reopened(storage);
    assert(reopened.IsHealthy());
    verify(reopened, cut == Storage::Cut::After);
  }
}

void TestAtomicBeforeAfterCutsAndLostAcknowledgements() {
  Storage missing;
  CheckCuts(missing,
    [](Journal& journal) { return journal.EstablishScopedIdentity("kufr-001", true); },
    [](const Journal& journal, bool committed) {
      assert(journal.CurrentEpoch() == (committed ? 1u : 0u));
      assert(journal.Origin() == (committed ? Journal::StateOrigin::Scoped : Journal::StateOrigin::Missing));
    });
  for (uint8_t version = 1; version <= 3; ++version) {
    CheckCuts(Legacy(version, false),
      [](Journal& journal) { return journal.EstablishScopedIdentity("kufr-001"); },
      [version](const Journal& journal, bool committed) {
        assert(journal.CurrentEpoch() == (committed ? 1u : 0u));
        assert(journal.Origin() == (committed ? Journal::StateOrigin::Scoped : Journal::StateOrigin::Legacy));
        if (version != 1) {
          assert(journal.FindHistorical(0, FirstId, 1, 101) == Lookup::Same);
          assert(journal.IsEvidencePinned(0, FirstId, 1, 101));
          assert(journal.WasTimedPresentationCompletedHistorical(0, FirstId, 1, 101) == (version == 3));
        }
      });
    // Resolving legacy pending also migrates bytes, retaining the uncertain ID.
    CheckCuts(Legacy(version, true),
      [](Journal& journal) { return journal.Resolve(61); },
      [version](const Journal& journal, bool committed) {
        assert(journal.HasPending() != committed);
        assert(journal.ReadyMask() == (committed ? 61 : 63));
        assert(journal.FindHistorical(0, SecondId, 2, version == 1 ? 0 : 202) == Lookup::Same);
      });
  }
  CheckCuts(Provisioned(),
    [](Journal& journal) { return journal.BeginScoped(1, FirstId, 1, 101); },
    [](const Journal& journal, bool committed) {
      assert(journal.HasPending() == committed);
      assert(journal.FindScoped(1, FirstId, 1, 101) == (committed ? Lookup::Same : Lookup::Missing));
      assert(journal.ReadyMask() == 63);
    });
  CheckCuts(Pending(),
    [](Journal& journal) { return journal.Resolve(62); },
    [](const Journal& journal, bool committed) {
      assert(journal.HasPending() != committed);
      assert(journal.ReadyMask() == (committed ? 62 : 63));
      assert(journal.FindScoped(1, FirstId, 1, 101) == Lookup::Same);
    });
  CheckCuts(Pending(),
    [](Journal& journal) { return journal.ResolveRestored(1); },
    [](const Journal& journal, bool committed) {
      assert(journal.HasPending() != committed);
      assert(journal.ReadyMask() == 63);
    });
  CheckCuts(Completed(),
    [](Journal& journal) { return journal.ConfirmTimedPresentationScoped(1, FirstId, 1, 101); },
    [](const Journal& journal, bool committed) {
      assert(journal.WasTimedPresentationCompletedScoped(1, FirstId, 1, 101) == committed);
      assert(journal.IsEvidencePinned(1, FirstId, 1, 101));
    });
  CheckCuts(Completed(true),
    [](Journal& journal) { return journal.ReleaseEvidencePin(1, FirstId, 1, 101); },
    [](const Journal& journal, bool committed) {
      assert(journal.IsEvidencePinned(1, FirstId, 1, 101) != committed);
      assert(journal.WasTimedPresentationCompletedScoped(1, FirstId, 1, 101));
      assert(journal.FindScoped(1, FirstId, 1, 101) == Lookup::Same);
    });
  Storage retention = Completed(true);
  {
    Journal journal(retention);
    assert(journal.BeginScoped(1, SecondId, 2, 202));
    assert(journal.ResolveRestored(2));
    assert(journal.ReleaseEvidencePin(1, SecondId, 2, 202));
  }
  CheckCuts(retention,
    [](Journal& journal) { return journal.AdvanceEpochAndCompact(); },
    [](const Journal& journal, bool committed) {
      assert(journal.CurrentEpoch() == (committed ? 2u : 1u));
      assert(journal.HistoryCount() == (committed ? 1u : 2u));
      assert(journal.FindHistorical(1, FirstId, 1, 101) == Lookup::Same);
      assert(journal.IsEvidencePinned(1, FirstId, 1, 101));
      assert(journal.WasTimedPresentationCompletedScoped(1, FirstId, 1, 101));
      assert(journal.FindHistorical(1, SecondId, 2, 202) == (committed ? Lookup::Missing : Lookup::Same));
      assert(journal.FindScoped(1, SecondId, 2, 202) == (committed ? Lookup::StaleEpoch : Lookup::Same));
    });
}

void AssertRejected(const Storage& storage) {
  Storage copy = storage;
  Journal journal(copy);
  assert(!journal.IsHealthy() && journal.HasPending());
  assert(journal.Origin() == Journal::StateOrigin::Unavailable);
  assert(journal.FindScoped(1, FirstId, 1, 101) == Lookup::Unavailable);
  assert(!journal.AdvanceEpochAndCompact());
  assert(!journal.EstablishScopedIdentity("kufr-001", true));
}

void TestIntegrityStructuralValidationAndReadFailure() {
  const Storage baseline = Completed(true);
  // A flipped bit anywhere in header, binding, active entry, padding or CRC
  // must fail closed, even when the resulting UUID/mask is structurally valid.
  for (size_t index = 0; index < baseline.size; ++index) {
    Storage corrupt = baseline;
    corrupt.bytes[index] ^= 1;
    AssertRejected(corrupt);
  }
  Storage corrupt = baseline;
  corrupt.failRead = true;
  AssertRejected(corrupt);
  for (size_t size : {size_t(0), size_t(43), size_t(44), LegacySize, Journal::RecordSize - 1}) {
    corrupt = baseline;
    corrupt.size = size;
    AssertRejected(corrupt);
  }
  corrupt = baseline;
  Write64(corrupt.bytes.data() + EpochOffset, 0);
  RefreshCrc(corrupt);
  AssertRejected(corrupt);
  corrupt = baseline;
  Write64(corrupt.bytes.data() + EntryOffset + 46, 2);
  RefreshCrc(corrupt);
  AssertRejected(corrupt);
  corrupt = baseline;
  corrupt.bytes[EntryOffset + 37] |= 4;
  RefreshCrc(corrupt);
  AssertRejected(corrupt);
  corrupt = baseline;
  corrupt.bytes[EntryOffset + EntrySize] = 1;
  RefreshCrc(corrupt);
  AssertRejected(corrupt);
  corrupt = baseline;
  corrupt.bytes[52] = static_cast<uint8_t>(Journal::StateOrigin::Missing);
  RefreshCrc(corrupt);
  AssertRejected(corrupt);
  corrupt = Pending();
  corrupt.bytes[EntryOffset + 37] = 0; // Pending history cannot be unpinned.
  RefreshCrc(corrupt);
  AssertRejected(corrupt);
}

}  // namespace

int main() {
  TestMissingStorageAndExplicitProvisioning();
  TestScopedDuplicatesConflictsAndPinOrdering();
  TestLegacyMigrationAndEvidence();
  TestRetainedUuidIsScopedByEpochAndDeviceBinding();
  TestAllPinnedPressureAndEpochOverflow();
  TestMoreThanThousandOperationsAcrossCompactionsAndReboots();
  TestAtomicBeforeAfterCutsAndLostAcknowledgements();
  TestIntegrityStructuralValidationAndReadFailure();
  return 0;
}

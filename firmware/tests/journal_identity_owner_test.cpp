#include <algorithm>
#include <array>
#include <cassert>
#include <cstdint>
#include <iostream>
#include <vector>

#include "../src/features/device_control/JournalIdentityOwner.h"

namespace {
using Journal = PersistentCommandJournal;
using Owner = JournalIdentityOwner;
using Validation = Owner::Validation;
using Phase = Owner::Phase;
constexpr char Device[] = "kufr-identity";
constexpr char FirstId[] = "523ef590-ce4d-4901-91b3-79de1bfe7e35";
constexpr char SecondId[] = "7a2fef87-0a80-4115-86f3-3a93ad984b82";
constexpr char UpperId[] = "523EF590-CE4D-4901-91B3-79DE1BFE7E35";

// Contract-level cuts expose either the complete old or complete new record.
// An After cut loses the acknowledgement; this does not prove NVS power safety.
class Storage final : public JournalStorage {
 public:
  enum class Cut { None, Before, After };
  JournalReadResult Read(uint8_t* target, size_t capacity, size_t& length) override {
    if (failRead || bytes.size() > capacity) return JournalReadResult::Failed;
    if (bytes.empty()) return JournalReadResult::Missing;
    std::copy(bytes.begin(), bytes.end(), target); length = bytes.size();
    return JournalReadResult::Found;
  }
  bool Write(const uint8_t* source, size_t length) override {
    ++writes;
    if (cut == Cut::Before) return false;
    bytes.assign(source, source + length);
    return cut != Cut::After;
  }
  std::vector<uint8_t> bytes;
  size_t writes = 0;
  bool failRead = false;
  Cut cut = Cut::None;
};
JournalIdentityView Unprovisioned() { return {true, false, ""}; }
JournalIdentityView Provisioned() { return {true, true, Device}; }
JournalEvidenceInventory Inventory() {
  JournalEvidenceInventory inventory;
  inventory.origin = JournalInventoryOrigin::Valid; inventory.deviceId = Device;
  inventory.generation = 1; inventory.complete = true;
  return inventory;
}
void Write64(uint8_t* target, uint64_t value) {
  for (unsigned byte = 0; byte < 8; ++byte) target[byte] = static_cast<uint8_t>(value >> (byte * 8));
}
void RefreshCrc(std::vector<uint8_t>& bytes, size_t offset, size_t length) {
  uint32_t crc = 0xffffffff;
  for (size_t index = offset; index < offset + length - 4; ++index) {
    crc ^= bytes[index];
    for (unsigned bit = 0; bit < 8; ++bit) crc = (crc >> 1) ^ ((crc & 1) ? 0xedb88320 : 0);
  }
  crc = ~crc;
  for (unsigned byte = 0; byte < 4; ++byte)
    bytes[offset + length - 4 + byte] = static_cast<uint8_t>(crc >> (byte * 8));
}
void RefreshCrc(std::vector<uint8_t>& bytes) { RefreshCrc(bytes, 0, bytes.size()); }
Storage Legacy(uint8_t version = 3, bool pending = false) {
  Storage storage;
  storage.bytes.assign(version == 1 ? 44 : 44 + Journal::HistoryCapacity * 45, 0);
  storage.bytes[0] = 'K'; storage.bytes[1] = 'J'; storage.bytes[2] = 'R'; storage.bytes[3] = '1';
  storage.bytes[4] = version; storage.bytes[5] = 63;
  if (pending) {
    storage.bytes[6] = 2;
    std::copy(SecondId, SecondId + 36, storage.bytes.begin() + 8);
  }
  if (version != 1) {
    storage.bytes[7] = pending ? 2 : 1;
    std::copy(FirstId, FirstId + 36, storage.bytes.begin() + 44);
    storage.bytes[80] = static_cast<uint8_t>(1 | (version == 3 ? 0x80 : 0));
    Write64(storage.bytes.data() + 81, 101);
    if (pending) {
      std::copy(SecondId, SecondId + 36, storage.bytes.begin() + 89);
      storage.bytes[125] = 2; Write64(storage.bytes.data() + 126, 202);
    }
  }
  return storage;
}
bool Begin(Owner& owner, bool factory) {
  return factory ? owner.BeginFactory(Device, Unprovisioned(), true) : owner.BeginLegacy(Provisioned(), true);
}
JournalIdentityView PreparingIdentity(bool factory) { return factory ? Unprovisioned() : Provisioned(); }
void Establish(Owner& owner, bool factory) {
  assert(Begin(owner, factory));
  assert(owner.ResumePreparation(PreparingIdentity(factory), true));
  assert(owner.Finalize(Provisioned(), Inventory(), Inventory(), true));
}

void FactoryAuthorityAndInventoryGates() {
  Storage journalStorage, markerStorage;
  Journal journal(journalStorage); Owner owner(markerStorage, journal);
  assert(owner.IsHealthy() && owner.Validate(Unprovisioned()) == Validation::Unestablished);
  assert(owner.Validate(Provisioned()) == Validation::MissingJournal);
  assert(!owner.BeginFactory(Device, Unprovisioned(), false));
  assert(!owner.BeginFactory(Device, Provisioned(), true));
  assert(!owner.BeginFactory("bad device", Unprovisioned(), true));
  assert(!owner.BeginFactory(std::string(65, 'a'), Unprovisioned(), true));
  assert(!owner.BeginLegacy(Provisioned(), true));
  assert(!markerStorage.writes && !journalStorage.writes);
  std::array<uint8_t, Journal::RecordSize> preview{};
  assert(journal.PreviewScopedIdentity(Device, true, preview));
  assert(owner.BeginFactory(Device, Unprovisioned(), true));
  assert(markerStorage.bytes.size() == Owner::PreparingRecordSize && markerStorage.writes == 1);
  assert(std::equal(preview.begin(), preview.end(), markerStorage.bytes.begin() + Owner::MarkerHeaderSize));
  assert(owner.CurrentPhase() == Phase::PreparingFactory && journal.IsStorageMissing());
  assert(owner.Validate(Provisioned()) == Validation::MissingJournal);
  assert(!owner.ResumePreparation(Provisioned(), true));
  assert(!owner.ResumePreparation(Unprovisioned(), false));
  assert(owner.ResumePreparation(Unprovisioned(), true));
  assert(!journal.IsStorageMissing() && journal.CurrentEpoch() == 1 && journal.ReadyMask() == 0);
  assert(journal.ScopedDeviceId() == Device && owner.Validate(Provisioned()) == Validation::Preparing);
  const auto before = journalStorage.writes;
  assert(owner.ResumePreparation(Unprovisioned(), true) && journalStorage.writes == before);
  assert(!owner.Finalize(Unprovisioned(), Inventory(), Inventory(), true));
  assert(!owner.Finalize(Provisioned(), Inventory(), Inventory(), false));
  for (unsigned field = 0; field < 9; ++field) {
    auto invalid = Inventory();
    if (field == 0) invalid.origin = JournalInventoryOrigin::Missing;
    if (field == 1) invalid.origin = JournalInventoryOrigin::Unavailable;
    if (field == 2) invalid.complete = false;
    if (field == 3) invalid.deviceId = "different-device";
    if (field == 4) invalid.generation = 0;
    if (field == 5) invalid.count = JournalEvidenceInventory::Capacity + 1;
    if (field == 6) invalid.ambiguous = true;
    if (field == 7 || field == 8) {
      invalid.count = 1; invalid.references[0] = {1, FirstId, 1, field == 7 ? uint64_t{101} : uint64_t{0}};
    }
    assert(!owner.Finalize(Provisioned(), invalid, Inventory(), true));
    assert(!owner.Finalize(Provisioned(), Inventory(), invalid, true));
  }
  assert(owner.Finalize(Provisioned(), Inventory(), Inventory(), true));
  assert(owner.CurrentPhase() == Phase::Established && markerStorage.bytes.size() == Owner::EstablishedRecordSize);
  assert(owner.Validate(Provisioned()) == Validation::Ready);
  assert(!owner.BeginFactory(Device, Unprovisioned(), true) && !owner.BeginLegacy(Provisioned(), true));
  assert(journal.AdvanceEpochAndCompact() && owner.Validate(Provisioned()) == Validation::Ready);
  assert(journal.PersistReadyMask(63));
  assert(journal.BeginScoped(2, FirstId, 1, 101));
  assert(owner.Validate(Provisioned()) == Validation::InspectionRequired);
  assert(journal.ResolveRestored(1) && owner.Validate(Provisioned()) == Validation::Ready);
}

void LegacyProofAndFoundMissingOrigin() {
  for (uint8_t version : std::array<uint8_t, 3>{{1, 2, 3}}) {
    auto journalStorage = Legacy(version); Storage markerStorage;
    Journal journal(journalStorage); Owner owner(markerStorage, journal);
    const auto original = journalStorage.bytes;
    std::array<uint8_t, Journal::RecordSize> expected{};
    assert(journal.PreviewScopedIdentity(Device, false, expected) && journalStorage.bytes == original);
    assert(!owner.BeginLegacy(Provisioned(), false));
    assert(!owner.BeginLegacy(Unprovisioned(), true));
    assert(owner.BeginLegacy(Provisioned(), true) && journalStorage.bytes == original);
    assert(owner.CurrentPhase() == Phase::PreparingLegacy);
    assert(owner.ResumePreparation(Provisioned(), true));
    std::array<uint8_t, Journal::RecordSize> exported{};
    exported.fill(0xff);
    assert(journal.ExportRecord(exported) && exported == expected);
    assert(journal.WasLegacyTimedPresentationCompleted(FirstId, 1) == (version == 3));
    if (version != 1) {
      JournalHistoryEntry entry;
      assert(journal.ReadHistoryEntry(0, entry));
      assert(entry.identity.epoch == 0 && entry.identity.operationId == FirstId && entry.identity.fingerprint == 101);
      assert(entry.evidencePinned && entry.timedCompleted == (version == 3));
      auto ambiguous = Inventory(); ambiguous.ambiguous = true;
      ambiguous.count = 1; ambiguous.references[0] = entry.identity;
      assert(owner.Finalize(Provisioned(), ambiguous, Inventory(), true));
    } else {
      assert(owner.Finalize(Provisioned(), Inventory(), Inventory(), true));
    }
    Journal reopenedJournal(journalStorage); Owner reopened(markerStorage, reopenedJournal);
    assert(reopened.Validate(Provisioned()) == Validation::Ready);
    assert(reopenedJournal.WasLegacyTimedPresentationCompleted(FirstId, 1) == (version == 3));
  }
  Storage journalStorage, markerStorage;
  Journal original(journalStorage);
  assert(original.IsStorageMissing() && original.PersistReadyMask(63));
  assert(!original.IsStorageMissing() && original.Origin() == Journal::StateOrigin::Missing);
  Journal journal(journalStorage); Owner owner(markerStorage, journal);
  assert(!journal.IsStorageMissing() && journal.Origin() == Journal::StateOrigin::Missing);
  assert(!journal.EstablishScopedIdentity(Device)); // Blob presence never proves binding authority.
  assert(!owner.BeginFactory(Device, Unprovisioned(), true));
  assert(!owner.BeginLegacy(Provisioned(), false));
  assert(owner.BeginLegacy(Provisioned(), true) && owner.ResumePreparation(Provisioned(), true));
  assert(journal.ReadyMask() == 63 && owner.Finalize(Provisioned(), Inventory(), Inventory(), true));
}

void RefusePendingMissingAndChangedPreparations() {
  auto pendingStorage = Legacy(3, true); Storage markerStorage;
  Journal pending(pendingStorage); Owner pendingOwner(markerStorage, pending);
  const auto pendingBytes = pendingStorage.bytes;
  assert(pendingOwner.Validate(Provisioned()) == Validation::InspectionRequired);
  assert(!pendingOwner.BeginLegacy(Provisioned(), true) && pendingStorage.bytes == pendingBytes && !markerStorage.writes);
  std::array<uint8_t, Journal::RecordSize> preview{}; preview.fill(0xff);
  assert(!pending.PreviewScopedIdentity(Device, false, preview));
  assert(std::all_of(preview.begin(), preview.end(), [](uint8_t value) { return value == 0; }));
  {
    Storage source, marker;
    Journal journal(source); Owner owner(marker, journal);
    assert(owner.BeginFactory(Device, Unprovisioned(), true));
    assert(journal.PersistReadyMask(0)); // Unexpected FOUND unscoped record, even with identical empty state.
    assert(owner.Validate(Unprovisioned()) == Validation::IdentityMismatch);
    const auto writes = source.writes;
    assert(!owner.ResumePreparation(Unprovisioned(), true) && source.writes == writes);
  }
  {
    auto source = Legacy(); Storage marker;
    Journal journal(source); Owner owner(marker, journal);
    assert(owner.BeginLegacy(Provisioned(), true));
    assert(journal.PersistReadyMask(62));
    assert(owner.Validate(Provisioned()) == Validation::IdentityMismatch);
    assert(!owner.ResumePreparation(Provisioned(), true));
    source.bytes.clear();
    Journal lost(source); Owner reopened(marker, lost);
    assert(reopened.Validate(Provisioned()) == Validation::MissingJournal);
    assert(!reopened.ResumePreparation(Provisioned(), true));
  }
  {
    Storage source, marker;
    Journal journal(source); Owner owner(marker, journal); Establish(owner, true);
    auto wrong = Provisioned(); wrong.deviceId = "different-device";
    assert(owner.Validate(wrong) == Validation::IdentityMismatch);
    assert(owner.Validate({false, false, ""}) == Validation::IdentityUnavailable);
    assert(owner.Validate({true, false, Device}) == Validation::IdentityMismatch);
    source.bytes.clear();
    Journal lost(source); Owner reopened(marker, lost);
    assert(reopened.Validate(Provisioned()) == Validation::MissingJournal);
    assert(!reopened.BeginFactory(Device, Unprovisioned(), true));
  }
  {
    Storage source, marker;
    Journal journal(source); Owner owner(marker, journal); Establish(owner, true);
    marker.bytes.clear();
    Owner missingMarker(marker, journal);
    assert(missingMarker.Validate(Provisioned()) == Validation::MissingMarker);
    assert(!missingMarker.BeginLegacy(Provisioned(), true));
  }
  {
    Storage source, marker;
    Journal journal(source); Owner owner(marker, journal); Establish(owner, true);
    Write64(marker.bytes.data() + 8, 2); RefreshCrc(marker.bytes);
    Owner tooNew(marker, journal);
    assert(tooNew.Validate(Provisioned()) == Validation::IdentityMismatch);
  }
}

void EveryPreparationCommitCut() {
  for (bool factory : {false, true}) {
    for (unsigned boundary = 1; boundary <= 3; ++boundary) {
      for (auto cut : std::array<Storage::Cut, 2>{{Storage::Cut::Before, Storage::Cut::After}}) {
        Storage source = factory ? Storage{} : Legacy(); Storage marker;
        Journal journal(source); Owner owner(marker, journal);
        if (boundary > 1) assert(Begin(owner, factory));
        if (boundary > 2) assert(owner.ResumePreparation(PreparingIdentity(factory), true));
        if (boundary == 2) source.cut = cut;
        else marker.cut = cut;
        const bool result = boundary == 1 ? Begin(owner, factory)
            : boundary == 2 ? owner.ResumePreparation(PreparingIdentity(factory), true)
            : owner.Finalize(Provisioned(), Inventory(), Inventory(), true);
        assert(!result && !owner.IsHealthy());
        const auto sourceWrites = source.writes, markerWrites = marker.writes;
        assert(!Begin(owner, factory));
        assert(!owner.ResumePreparation(PreparingIdentity(factory), true));
        assert(!owner.Finalize(Provisioned(), Inventory(), Inventory(), true));
        assert(source.writes == sourceWrites && marker.writes == markerWrites);
        source.cut = Storage::Cut::None; marker.cut = Storage::Cut::None;
        Journal reopenedJournal(source); Owner reopened(marker, reopenedJournal);
        assert(reopened.IsHealthy());
        if (boundary == 1 && cut == Storage::Cut::Before) assert(Begin(reopened, factory));
        if (boundary == 3 && cut == Storage::Cut::After) {
          assert(reopened.Validate(Provisioned()) == Validation::Ready);
        } else {
          assert(reopened.CurrentPhase() == (factory ? Phase::PreparingFactory : Phase::PreparingLegacy));
          if (factory && reopenedJournal.IsStorageMissing()) {
            assert(reopened.Validate(Provisioned()) == Validation::MissingJournal);
            assert(!reopened.ResumePreparation(Provisioned(), true));
          }
          assert(reopened.ResumePreparation(PreparingIdentity(factory), true));
          assert(reopened.Finalize(Provisioned(), Inventory(), Inventory(), true));
        }
        assert(reopened.Validate(Provisioned()) == Validation::Ready);
        assert(factory || reopenedJournal.WasLegacyTimedPresentationCompleted(FirstId, 1));
      }
    }
  }
}

void MarkerCodecCorruptionAndReadFailures() {
  Storage source, marker;
  Journal journal(source); Owner owner(marker, journal);
  assert(owner.BeginFactory(Device, Unprovisioned(), true));
  const auto preparing = marker.bytes;
  assert(owner.ResumePreparation(Unprovisioned(), true));
  assert(owner.Finalize(Provisioned(), Inventory(), Inventory(), true));
  const auto established = marker.bytes;
  for (const auto& valid : {preparing, established}) {
    for (unsigned field = 0; field < 10; ++field) {
      auto corrupt = valid;
      if (field == 0) corrupt.back() ^= 1; // CRC error without updating it.
      if (field == 1) { corrupt[4] = 2; RefreshCrc(corrupt); }
      if (field == 2) { corrupt[5] = 4; RefreshCrc(corrupt); }
      if (field == 3) { corrupt[7] = 1; RefreshCrc(corrupt); }
      if (field == 4) { corrupt[6] = 65; RefreshCrc(corrupt); }
      if (field == 5) { corrupt[6] = 0; RefreshCrc(corrupt); }
      if (field == 6) { corrupt[16 + sizeof(Device) - 1] = 1; RefreshCrc(corrupt); }
      if (field == 7) { Write64(corrupt.data() + 8, 0); RefreshCrc(corrupt); }
      if (field == 8) corrupt.pop_back();
      if (field == 9) corrupt.push_back(0);
      Storage broken; broken.bytes = corrupt;
      Owner rejected(broken, journal);
      assert(!rejected.IsHealthy() && rejected.Validate(Provisioned()) == Validation::MarkerUnavailable);
      assert(!rejected.ResumePreparation(Provisioned(), true) && !broken.writes);
    }
  }
  {
    Storage broken; broken.bytes = preparing;
    broken.bytes[Owner::MarkerHeaderSize + 54 + sizeof(Device) - 1] = 1;
    RefreshCrc(broken.bytes, Owner::MarkerHeaderSize, Journal::RecordSize); RefreshCrc(broken.bytes);
    Owner rejected(broken, journal); assert(!rejected.IsHealthy()); // Valid outer CRC cannot bless journal padding.
  }
  {
    Storage broken; broken.bytes = preparing; broken.bytes[Owner::MarkerHeaderSize + 5] = 63;
    RefreshCrc(broken.bytes, Owner::MarkerHeaderSize, Journal::RecordSize); RefreshCrc(broken.bytes);
    Owner rejected(broken, journal); assert(!rejected.IsHealthy()); // Factory initialization cannot claim a ready mask.
  }
  {
    Storage broken; broken.failRead = true;
    Owner rejected(broken, journal); assert(!rejected.IsHealthy() && !broken.writes);
  }
  {
    Storage brokenSource; brokenSource.failRead = true;
    Journal rejectedJournal(brokenSource); Owner rejected(marker, rejectedJournal);
    assert(!rejected.IsHealthy() && rejected.Validate(Provisioned()) == Validation::JournalUnavailable);
  }
  {
    Storage brokenSource; brokenSource.bytes = source.bytes; brokenSource.bytes.back() ^= 1;
    Journal rejectedJournal(brokenSource); Owner rejected(marker, rejectedJournal);
    assert(!rejected.IsHealthy() && !rejectedJournal.IsStorageMissing());
    JournalHistoryEntry entry{{1, FirstId, 1, 101}, true, true};
    assert(!rejectedJournal.ReadHistoryEntry(0, entry) && entry.identity.operationId.empty() && !entry.evidencePinned);
    std::array<uint8_t, Journal::RecordSize> bytes{}; bytes.fill(0xff);
    assert(!rejectedJournal.ExportRecord(bytes));
    assert(std::all_of(bytes.begin(), bytes.end(), [](uint8_t value) { return value == 0; }));
  }
}

void ExactPinRetentionAndLostAcknowledgement() {
  for (auto cut : std::array<Storage::Cut, 2>{{Storage::Cut::Before, Storage::Cut::After}}) {
    auto source = Legacy(); Storage marker;
    Journal journal(source); Owner owner(marker, journal); Establish(owner, false);
    JournalHistoryEntry entry;
    assert(journal.ReadHistoryEntry(0, entry) && entry.evidencePinned && entry.timedCompleted);
    assert(!journal.ReadHistoryEntry(Journal::HistoryCapacity, entry) && entry.identity.operationId.empty());
    assert(journal.ReleaseEvidencePin(0, FirstId, 1, 101));
    const auto writes = source.writes;
    assert(!journal.RetainEvidencePin(1, FirstId, 1, 101));
    assert(!journal.RetainEvidencePin(0, FirstId, 2, 101));
    assert(!journal.RetainEvidencePin(0, FirstId, 1, 102));
    assert(source.writes == writes);
    source.cut = cut;
    assert(!journal.RetainEvidencePin(0, UpperId, 1, 101));
    assert(!journal.IsHealthy() && !owner.IsHealthy());
    assert(!journal.RetainEvidencePin(0, FirstId, 1, 101));
    source.cut = Storage::Cut::None;
    Journal reopened(source); Owner reopenedOwner(marker, reopened);
    assert(reopened.IsEvidencePinned(0, FirstId, 1, 101) == (cut == Storage::Cut::After));
    assert(reopened.RetainEvidencePin(0, FirstId, 1, 101));
    const auto retainedWrites = source.writes;
    assert(reopened.RetainEvidencePin(0, UpperId, 1, 101) && source.writes == retainedWrites);
    assert(reopened.BeginScoped(1, SecondId, 2, 202));
    assert(!reopened.RetainEvidencePin(0, FirstId, 1, 101));
    assert(reopenedOwner.Validate(Provisioned()) == Validation::InspectionRequired);
  }
}
}

int main() {
  FactoryAuthorityAndInventoryGates();
  LegacyProofAndFoundMissingOrigin();
  RefusePendingMissingAndChangedPreparations();
  EveryPreparationCommitCut();
  MarkerCodecCorruptionAndReadFailures();
  ExactPinRetentionAndLostAcknowledgement();
  std::cout << "Journal identity service preparation, marker integrity and exact evidence retention passed\n";
}

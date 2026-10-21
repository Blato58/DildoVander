#include <algorithm>
#include <array>
#include <cassert>
#include <cstdint>
#include <iostream>
#include <limits>
#include <string>
#include <vector>

#include "../src/features/keepsake/KeepsakeCommittedInventory.h"
#include "../src/features/keepsake/KeepsakePersistenceCodec.h"
#include "../src/features/device_control/PersistentCommandJournal.h"

namespace {
constexpr char Device[] = "online-inventory";
using Store = KeepsakeCommittedInventory;
class Storage final : public JournalStorage {
 public:
  enum class Cut { None, Before, After };
  JournalReadResult Read(uint8_t* target, size_t capacity, size_t& length) override {
    ++reads;
    if (failRead || bytes.size() > capacity) return JournalReadResult::Failed;
    if (bytes.empty()) return JournalReadResult::Missing;
    length = bytes.size(); std::copy(bytes.begin(), bytes.end(), target);
    return JournalReadResult::Found;
  }
  bool Write(const uint8_t* source, size_t length) override {
    ++writes;
    if (cut == Cut::Before) return false;
    bytes.assign(source, source + length);
    return cut != Cut::After;
  }
  void Set(const std::string& text) { bytes.assign(text.begin(), text.end()); bytes.push_back(0); }
  std::vector<uint8_t> bytes;
  bool failRead = false;
  Cut cut = Cut::None;
  size_t reads = 0, writes = 0;
};
class Barrier final : public JournalReconciliationBarrier {
 public:
  bool TryEnter() override { ++attempts; if (busy) return false; busy = true; return true; }
  void Leave() override { assert(busy); busy = false; ++leaves; }
  bool busy = false;
  unsigned attempts = 0, leaves = 0;
};
std::string Id(unsigned number) {
  const auto decimal = std::to_string(number);
  return "00000000-0000-4000-8000-" + std::string(12 - decimal.size(), '0') + decimal;
}
KeepsakeOutcome Candidate(unsigned number = 1, uint64_t epoch = 1) {
  KeepsakeOutcome item{Id(number), Id(1000 + number), Id(2000 + number), Id(3000 + number), Id(4000), 1, 2};
  item.compartment = static_cast<uint8_t>(number % 6 + 1); item.operationEpoch = epoch; item.physicalFingerprint = 101;
  return item;
}
KeepsakeDisposition Disposition(const KeepsakeOutcome& item, const std::string& reason = "outcome_delivered") {
  return {item.operationId, item.outcomeId, reason, item.operationEpoch,
      item.physicalOperationId, item.compartment, item.physicalFingerprint};
}
JournalEvidenceInventory Read(Store& store) {
  JournalEvidenceInventory inventory;
  assert(store.ReadCommitted(inventory));
  return inventory;
}
std::string HexCrc(const std::string& value) {
  uint32_t crc = 0xffffffff;
  for (unsigned char byte : value) {
    crc ^= byte;
    for (unsigned bit = 0; bit < 8; ++bit) crc = (crc >> 1) ^ ((crc & 1) ? 0xedb88320 : 0);
  }
  crc = ~crc;
  std::string text(8, '0'); const char* digits = "0123456789abcdef";
  for (unsigned index = 0; index < 8; ++index) text[7 - index] = digits[(crc >> (index * 4)) & 15];
  return text;
}
std::string Envelope(const KeepsakeOutboxState& state, const std::string& generation = "1", const std::string& device = Device) {
  const std::string prefix = "{\"schemaVersion\":4,\"deviceId\":\"" + device + "\",\"inventoryGeneration\":\""
      + generation + "\",\"payload\":" + keepsake_persistence::Encode(state);
  return prefix + ",\"crc32\":\"" + HexCrc(prefix) + "\"}";
}
std::string Rechecksum(const std::string& value) {
  const auto offset = value.rfind(",\"crc32\":"); assert(offset != std::string::npos);
  const auto prefix = value.substr(0, offset);
  return prefix + ",\"crc32\":\"" + HexCrc(prefix) + "\"}";
}

void MissingLegacyBindingAndRamIsolation() {
  Storage storage; Store store(storage);
  auto inventory = Read(store);
  assert(inventory.origin == JournalInventoryOrigin::Missing && !inventory.complete && !store.IsBound());
  KeepsakeOutbox ram;
  assert(ram.Prepare(Candidate()));
  assert(Read(store).origin == JournalInventoryOrigin::Missing); // RAM is never the committed inventory.
  assert(store.Commit({}) && storage.bytes.empty() && !storage.writes);
  assert(!store.Establish(Device, false) && !store.Establish("bad device", true));
  assert(store.Commit(ram.State()));
  inventory = Read(store);
  assert(inventory.origin == JournalInventoryOrigin::Valid && !inventory.complete && inventory.ambiguous
      && !inventory.generation && inventory.deviceId.empty());
  assert(!store.Establish(Device, false));
  assert(store.Establish(Device, true));
  inventory = Read(store);
  assert(inventory.complete && !inventory.ambiguous && inventory.generation == 1 && inventory.deviceId == Device && inventory.count == 1);
  assert(!store.Establish("other-device", true));
  const auto writes = storage.writes;
  assert(store.Establish(Device, true) && storage.writes == writes);
  assert(ram.Queue(Candidate()) == KeepsakeQueueResult::VolatilePendingPersistence);
  assert(ram.RequestDeliveryAcknowledgement(Candidate()));
  assert(Read(store).count == 1 && Read(store).generation == 1); // Queue and retirement mailbox do not commit.
  assert(store.Commit(ram.State()) && Read(store).generation == 2);
  Storage legacy;
  auto old = Candidate(9, 0); old.physicalFingerprint = 0;
  const std::string body = keepsake_persistence::Body(old, true);
  legacy.Set("[" + body + "]");
  Store oldStore(legacy);
  assert(oldStore.IsHealthy() && !Read(oldStore).complete);
  assert(oldStore.Establish(Device, true));
  inventory = Read(oldStore);
  assert(inventory.complete && inventory.ambiguous && inventory.references[0].epoch == 0
      && inventory.references[0].fingerprint == 0);
  KeepsakeOutboxState restored;
  assert(oldStore.ReadState(restored) && restored.pending[0].operationEpoch == 0 && !restored.pending[0].physicalFingerprint);
}

void ActualCodecIntegrityAndPoisoning() {
  KeepsakeOutboxState state{{Candidate()}, {}, {Candidate()}};
  const auto valid = Envelope(state);
  Storage source; source.Set(valid); Store store(source);
  assert(store.IsBound() && Read(store).generation == 1);
  for (const char* generation : {"0", "01", "18446744073709551616", "-1", "1.0", ""}) {
    Storage invalid; invalid.Set(Envelope(state, generation)); Store rejected(invalid);
    assert(!rejected.IsHealthy());
  }
  for (const char* device : {"", "bad device"}) {
    Storage invalid; invalid.Set(Envelope(state, "1", device)); Store rejected(invalid);
    assert(!rejected.IsHealthy());
  }
  std::vector<std::string> corrupt;
  auto changed = valid; changed[changed.size() - 3] ^= 1; corrupt.push_back(changed);
  changed = valid; changed.insert(changed.find("\"deviceId\""), "\"deviceId\":\"online-inventory\","); corrupt.push_back(Rechecksum(changed));
  changed = valid; changed.insert(changed.find("\"inventoryGeneration\""), "\"inventoryGeneration\":\"1\","); corrupt.push_back(Rechecksum(changed));
  changed = valid; changed.insert(changed.find("\"physicalFingerprint\""), "\"physicalFingerprint\":\"101\","); corrupt.push_back(Rechecksum(changed));
  changed = valid; changed.insert(changed.find("\"compartment\""), "\"compartment\":2,"); corrupt.push_back(Rechecksum(changed));
  changed = valid; changed.insert(1, " "); corrupt.push_back(changed); // Bound bytes are canonical, including padding.
  corrupt.push_back(valid.substr(0, valid.size() - 1));
  corrupt.push_back(valid + "garbage");
  for (const auto& bytes : corrupt) {
    Storage invalid; invalid.Set(bytes); Store rejected(invalid);
    JournalEvidenceInventory unavailable;
    assert(!rejected.IsHealthy() && !rejected.ReadCommitted(unavailable)
        && unavailable.origin == JournalInventoryOrigin::Unavailable && !invalid.writes);
  }
  source.failRead = true;
  JournalEvidenceInventory unavailable;
  assert(!store.ReadCommitted(unavailable) && !store.IsHealthy());
  source.failRead = false;
  assert(!store.ReadCommitted(unavailable)); // Clearing the port failure is not an authoritative reopen.
  assert(store.Reopen() && Read(store).generation == 1);
  source.Set(Envelope(state, "2"));
  assert(!store.ReadCommitted(unavailable) && !store.IsHealthy()); // Out-of-owner byte changes require reopen too.
  assert(store.Reopen() && Read(store).generation == 2);
  source.Set(Envelope(state, "18446744073709551615"));
  assert(store.Reopen() && Read(store).generation == std::numeric_limits<uint64_t>::max());
  auto next = state; next.prepared.clear();
  const auto original = source.bytes; const auto writes = source.writes;
  assert(!store.Commit(next) && !store.IsHealthy() && source.bytes == original && source.writes == writes);
  assert(store.Reopen() && store.Commit(state) && source.writes == writes);
}

void CompleteReferencesAndUnknownLedger() {
  KeepsakeOutboxState state;
  for (unsigned index = 1; index <= KeepsakeOutbox::Capacity; ++index) {
    auto item = Candidate(index, std::numeric_limits<uint64_t>::max());
    item.generation = INT32_MAX; item.deviceRevision = INT32_MAX; item.physicalFingerprint = std::numeric_limits<uint64_t>::max();
    state.pending.push_back(item); state.prepared.push_back(item);
  }
  for (unsigned index = 1; index <= KeepsakeOutbox::QuarantineCapacity; ++index) {
    auto item = Candidate(100 + index, std::numeric_limits<uint64_t>::max());
    item.physicalFingerprint = std::numeric_limits<uint64_t>::max();
    state.quarantined.push_back(Disposition(item, "physical_outcome_unknown"));
  }
  Storage storage; Store store(storage);
  assert(store.Establish(Device, true) && store.Commit(state));
  const auto inventory = Read(store);
  assert(inventory.complete && inventory.ambiguous && inventory.count == 40 && inventory.count <= inventory.Capacity);
  assert(storage.bytes.size() > 16384 && storage.bytes.size() <= Store::MaximumRecordSize);
  KeepsakeOutboxState decoded; assert(store.ReadState(decoded));
  assert(decoded.quarantined[0].physicalOperationId == state.quarantined[0].physicalOperationId
      && decoded.quarantined[0].physicalFingerprint == std::numeric_limits<uint64_t>::max());
  auto missing = decoded; missing.quarantined.clear();
  assert(!store.Commit(missing)); // Public updates cannot silently erase bound retirement history.
  KeepsakeDisposition legacyDisposition{};
  legacyDisposition.operationId = Id(1);
  legacyDisposition.outcomeId = Id(1001);
  legacyDisposition.reason = "outcome_delivered";
  KeepsakeOutboxState legacyState;
  legacyState.quarantined.push_back(legacyDisposition);
  Storage legacy;
  legacy.Set(keepsake_persistence::Encode(legacyState));
  Store unknown(legacy); assert(unknown.Establish(Device, true));
  assert(Read(unknown).ambiguous); // Old dispositions have no invented physical tuple.
  auto conflict = Candidate();
  auto different = conflict; ++different.physicalFingerprint;
  Storage conflicts; Store conflicting(conflicts); assert(conflicting.Establish(Device, true));
  assert(conflicting.Commit({{conflict}, {Disposition(different)}, {conflict}}));
  assert(Read(conflicting).ambiguous && Read(conflicting).count == 2);
  auto otherBackend = conflict;
  otherBackend.operationId = Id(901); otherBackend.outcomeId = Id(1901); ++otherBackend.physicalFingerprint;
  Storage physicalConflictBytes; Store physicalConflict(physicalConflictBytes);
  assert(physicalConflict.Establish(Device, true));
  assert(physicalConflict.Commit({{otherBackend}, {Disposition(conflict)}, {otherBackend}}));
  const auto physicalConflictInventory = Read(physicalConflict);
  assert(physicalConflictInventory.ambiguous && physicalConflictInventory.count == 2);
  // Even different backend IDs cannot let a terminal receipt shadow the old
  // physical tuple when a live record reuses its UUID with conflicting content.
  bool foundTerminal = false, foundLive = false;
  for (size_t index = 0; index < physicalConflictInventory.count; ++index) {
    foundTerminal |= SameJournalOperationIdentity(physicalConflictInventory.references[index], KeepsakePhysicalIdentity(conflict));
    foundLive |= SameJournalOperationIdentity(physicalConflictInventory.references[index], KeepsakePhysicalIdentity(otherBackend));
  }
  assert(foundTerminal && foundLive);
  otherBackend.physicalFingerprint = conflict.physicalFingerprint;
  Storage physicalAliasBytes; Store physicalAlias(physicalAliasBytes);
  assert(physicalAlias.Establish(Device, true));
  assert(physicalAlias.Commit({{otherBackend}, {Disposition(conflict)}, {otherBackend}}));
  const auto aliasInventory = Read(physicalAlias);
  assert(aliasInventory.count == 1
      && SameJournalOperationIdentity(aliasInventory.references[0], KeepsakePhysicalIdentity(conflict)));
  // A matching physical tuple is still live until the pending/prepared bytes
  // are durably retired, regardless of the terminal receipt's backend IDs.
}

void BoundActiveReferenceRemovalRequiresExactReceipt() {
  for (bool pending : {false, true}) {
    Storage storage; Store store(storage); assert(store.Establish(Device, true));
    const auto original = Candidate();
    const KeepsakeOutboxState initial = pending ? KeepsakeOutboxState{{original}, {}, {}}
        : KeepsakeOutboxState{{}, {}, {original}};
    assert(store.Commit(initial));
    const auto committed = storage.bytes; const auto writes = storage.writes;
    assert(!store.Commit({}) && storage.bytes == committed && storage.writes == writes && store.IsHealthy());
    for (unsigned field = 0; field < 10; ++field) {
      auto changed = original;
      if (field == 0) changed.operationId = Id(900);
      if (field == 1) changed.outcomeId = Id(1900);
      if (field == 2) changed.proposalId = Id(2900);
      if (field == 3) changed.physicalOperationId = Id(3900);
      if (field == 4) changed.bootId = Id(4900);
      if (field == 5) ++changed.generation;
      if (field == 6) ++changed.deviceRevision;
      if (field == 7) changed.compartment = static_cast<uint8_t>(changed.compartment % 6 + 1);
      if (field == 8) ++changed.operationEpoch;
      if (field == 9) ++changed.physicalFingerprint;
      assert(!store.Commit({{changed}, {}, {changed}}));
      assert(storage.bytes == committed && storage.writes == writes);
    }
    auto conflicting = original; ++conflicting.physicalFingerprint;
    assert(!store.Commit({{}, {Disposition(conflicting)}, {}}));
    assert(storage.bytes == committed && storage.writes == writes);
    auto unknown = Disposition(original); unknown.physicalOperationId.clear(); unknown.compartment = 0; unknown.physicalFingerprint = 0;
    assert(!store.Commit({{}, {unknown}, {}}));
    assert(storage.bytes == committed && storage.writes == writes);
    assert(store.Commit({{original}, {}, {}})); // Identical preparation-to-pending promotion is valid.
    assert(store.Commit({{original}, {}, {original}}));
    assert(store.Commit({{}, {}, {original}})); // Removing one duplicate leaves the exact other reference.
    assert(Read(store).count == 1);
    const auto receipt = Disposition(original);
    assert(store.Commit({{}, {receipt}, {original}})); // Persist terminal receipt before normal removal.
    assert(Read(store).count == 1);
    assert(store.Commit({{}, {receipt}, {}}) && Read(store).count == 0);
    KeepsakeOutboxState saved; assert(store.ReadState(saved) && saved.quarantined.size() == 1);
  }
  Storage legacy; Store unbound(legacy);
  assert(unbound.Commit({{Candidate()}, {}, {Candidate()}}));
  assert(unbound.Commit({})); // Existing unbound producer behavior is unchanged.
}

void RetirementCommitCutsAndRestart() {
  for (unsigned boundary = 1; boundary <= 2; ++boundary) {
    for (auto cut : std::array<Storage::Cut, 2>{{Storage::Cut::Before, Storage::Cut::After}}) {
      Storage storage; Store store(storage); assert(store.Establish(Device, true));
      KeepsakeOutbox outbox; outbox.RetainRetirementLedger();
      const auto candidate = Candidate();
      assert(outbox.Prepare(candidate) && outbox.Queue(candidate) == KeepsakeQueueResult::VolatilePendingPersistence);
      assert(store.Commit(outbox.State()) && Read(store).generation == 2);
      assert(outbox.RequestDeliveryAcknowledgement(candidate));
      assert(Read(store).count == 1 && Read(store).generation == 2);
      unsigned calls = 0;
      assert(!outbox.ProcessDeliveryAcknowledgements([&](const KeepsakeOutboxState& state) {
        storage.cut = ++calls == boundary ? cut : Storage::Cut::None;
        const bool result = store.Commit(state);
        if (result) assert(Read(store).count == (calls == 1 ? 1u : 0u));
        return result;
      }));
      JournalEvidenceInventory unavailable;
      assert(!store.ReadCommitted(unavailable) && !store.IsHealthy());
      storage.cut = Storage::Cut::None;
      assert(store.Reopen());
      const auto after = Read(store);
      const bool removed = boundary == 2 && cut == Storage::Cut::After;
      assert(after.count == (removed ? 0u : 1u));
      assert(after.generation == (boundary == 1 && cut == Storage::Cut::Before ? 2u : removed ? 4u : 3u));
      KeepsakeOutboxState committed; assert(store.ReadState(committed));
      if (!committed.quarantined.empty()) {
        assert(ExactKeepsakeDisposition(committed.quarantined[0], candidate));
        KeepsakeOutbox rebooted; rebooted.RetainRetirementLedger();
        assert(rebooted.Restore(committed.pending, committed.quarantined, committed.prepared));
        assert(Read(store).count == (removed ? 0u : 1u)); // RAM restore/fence removal has no storage authority.
        assert(store.Commit(rebooted.State()) && Read(store).count == 0);
        assert(store.ReadState(committed) && committed.quarantined.size() == 1);
      }
    }
  }
  for (auto cut : std::array<Storage::Cut, 2>{{Storage::Cut::Before, Storage::Cut::After}}) {
    Storage storage; Store store(storage); storage.cut = cut;
    assert(!store.Establish(Device, true) && !store.IsHealthy());
    storage.cut = Storage::Cut::None; assert(store.Reopen());
    assert(store.IsBound() == (cut == Storage::Cut::After));
    assert(Read(store).origin == (cut == Storage::Cut::After ? JournalInventoryOrigin::Valid : JournalInventoryOrigin::Missing));
  }
}

void ExplicitPruningUsesMatchingCommittedJournal() {
  for (auto cut : std::array<Storage::Cut, 2>{{Storage::Cut::Before, Storage::Cut::After}}) {
    Storage storage, journalStorage; Store store(storage); Barrier barrier;
    PersistentCommandJournal journal(journalStorage);
    assert(journal.EstablishScopedIdentity(Device, true));
    const auto candidate = Candidate();
    assert(store.Establish(Device, true));
    assert(store.Commit({{}, {Disposition(candidate)}, {}}));
    assert(store.PruneRetired(journal, barrier)); // Current-epoch retirement is still needed as a fence.
    KeepsakeOutboxState state; assert(store.ReadState(state) && state.quarantined.size() == 1);
    assert(journal.AdvanceEpochAndCompact());
    barrier.busy = true; assert(!store.PruneRetired(journal, barrier)); barrier.busy = false;
    storage.cut = cut;
    assert(!store.PruneRetired(journal, barrier) && !store.IsHealthy() && !barrier.busy);
    storage.cut = Storage::Cut::None; assert(store.Reopen() && store.ReadState(state));
    assert(state.quarantined.size() == (cut == Storage::Cut::After ? 0u : 1u));
    assert(store.PruneRetired(journal, barrier) && store.ReadState(state) && state.quarantined.empty());
    assert(journal.FindScoped(1, candidate.physicalOperationId, candidate.compartment, candidate.physicalFingerprint)
        == CommandJournal::Lookup::StaleEpoch);
    assert(!journal.BeginScoped(1, candidate.physicalOperationId, candidate.compartment, candidate.physicalFingerprint));
  }
  Storage storage, journalStorage, wrongStorage; Store store(storage); Barrier barrier;
  PersistentCommandJournal journal(journalStorage), wrong(wrongStorage);
  const auto candidate = Candidate();
  assert(journal.EstablishScopedIdentity(Device, true) && journal.PersistReadyMask(63));
  assert(journal.BeginScoped(1, candidate.physicalOperationId, candidate.compartment, candidate.physicalFingerprint));
  assert(journal.ResolveRestored(candidate.compartment));
  assert(journal.ConfirmTimedPresentationScoped(1, candidate.physicalOperationId, candidate.compartment, candidate.physicalFingerprint));
  assert(store.Establish(Device, true) && store.Commit({{}, {Disposition(candidate)}, {}}));
  assert(journal.AdvanceEpochAndCompact());
  assert(store.PruneRetired(journal, barrier));
  KeepsakeOutboxState state; assert(store.ReadState(state) && state.quarantined.size() == 1); // Pinned exact history still exists.
  assert(wrong.EstablishScopedIdentity("other-device", true));
  assert(!store.PruneRetired(wrong, barrier) && !barrier.busy);
  assert(journal.ReleaseEvidencePin(1, candidate.physicalOperationId, candidate.compartment, candidate.physicalFingerprint));
  assert(journal.AdvanceEpochAndCompact());
  auto unknown = Candidate(9, 0); unknown.physicalFingerprint = 0;
  state.quarantined.push_back(Disposition(unknown, "physical_outcome_unknown"));
  assert(store.Commit(state));
  assert(store.PruneRetired(journal, barrier) && store.ReadState(state));
  assert(state.quarantined.size() == 1 && state.quarantined[0].reason == "physical_outcome_unknown");
  assert(Read(store).ambiguous);
}
}

int main() {
  MissingLegacyBindingAndRamIsolation();
  ActualCodecIntegrityAndPoisoning();
  CompleteReferencesAndUnknownLedger();
  BoundActiveReferenceRemovalRequiresExactReceipt();
  RetirementCommitCutsAndRestart();
  ExplicitPruningUsesMatchingCommittedJournal();
  std::cout << "Committed online inventory: explicit binding, exact durable references, retirement cuts and pruning passed\n";
}

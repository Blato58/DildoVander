#include <algorithm>
#include <array>
#include <cassert>
#include <functional>
#include <limits>
#include <memory>
#include <string>
#include <vector>

#include "../src/features/device_control/JournalIdentityOwner.h"
#include "../src/features/device_control/JournalReconciliation.h"

namespace {

using Journal = PersistentCommandJournal;
using Result = JournalReconciliation::Result;
using Identity = JournalOperationIdentity;
constexpr char Device[] = "kufr-001";
constexpr unsigned MixedOperations = 1152;

// Atomic old/new replacement, with either failure before commit or a lost ACK
// after commit. Snapshots survive journal/owner reconstruction in every test.
class Storage final : public JournalStorage {
 public:
  enum class Cut { None, Before, After };
  JournalReadResult Read(uint8_t* output, size_t capacity, size_t& length) override {
    if (failRead || bytes.size() > capacity) return JournalReadResult::Failed;
    if (bytes.empty()) return JournalReadResult::Missing;
    length = bytes.size();
    std::copy(bytes.begin(), bytes.end(), output);
    return JournalReadResult::Found;
  }
  bool Write(const uint8_t* input, size_t length) override {
    ++writes;
    if (onWrite) onWrite();
    const bool cutting = failAt && writes == failAt;
    if (cutting && cut == Cut::Before) return false;
    bytes.assign(input, input + length);
    return !(cutting && cut == Cut::After);
  }
  std::vector<uint8_t> bytes;
  size_t writes = 0, failAt = 0;
  Cut cut = Cut::None;
  bool failRead = false;
  std::function<void()> onWrite;
};

class Barrier final : public JournalReconciliationBarrier {
 public:
  bool TryEnter() override {
    ++attempts;
    if (held) return false;
    held = true;
    return true;
  }
  void Leave() override { assert(held); held = false; ++leaves; }
  bool held = false;
  unsigned attempts = 0, leaves = 0;
};

class Evidence final : public JournalEvidenceSource {
 public:
  explicit Evidence(Barrier& barrier) : barrier_(barrier) {}
  bool ReadCommitted(JournalEvidenceInventory& output) const override {
    assert(barrier_.held);
    ++reads;
    if (onRead) onRead();
    if (!readable) return false;
    output = committed_;
    return true;
  }
  bool Commit(const JournalEvidenceInventory& next) {
    if (!barrier_.TryEnter()) return false;
    CommitWhileHeld(next);
    barrier_.Leave();
    return true;
  }
  void CommitWhileHeld(const JournalEvidenceInventory& next) {
    assert(barrier_.held);
    committed_ = next;
    ++commits;
  }
  const JournalEvidenceInventory& Committed() const { return committed_; }
  // A mailbox/draft is intentionally not part of ReadCommitted authority.
  JournalEvidenceInventory volatileDraft;
  bool readable = true;
  mutable unsigned reads = 0;
  unsigned commits = 0;
  mutable std::function<void()> onRead;

 private:
  Barrier& barrier_;
  JournalEvidenceInventory committed_;
};

struct Durable {
  Storage journal, marker;
  Barrier barrier;
  Evidence online{barrier}, offline{barrier};
};

struct Session {
  explicit Session(Durable& durable)
      : journal(durable.journal), identity(durable.marker, journal),
        reconciliation(journal, identity, durable.online, durable.offline, durable.barrier) {}
  Journal journal;
  JournalIdentityOwner identity;
  JournalReconciliation reconciliation;
};

JournalIdentityView View() { return {true, true, Device}; }

JournalEvidenceInventory Inventory(const std::vector<Identity>& references = {}, uint64_t generation = 1) {
  JournalEvidenceInventory result;
  result.origin = JournalInventoryOrigin::Valid;
  result.deviceId = Device;
  result.generation = generation;
  result.complete = true;
  result.count = references.size();
  assert(result.count <= result.references.size());
  std::copy(references.begin(), references.end(), result.references.begin());
  return result;
}

void Provision(Durable& durable) {
  Session session(durable);
  const JournalIdentityView fresh{true, false, ""};
  assert(session.identity.BeginFactory(Device, fresh, true));
  assert(session.identity.ResumePreparation(fresh, true));
  assert(durable.online.Commit(Inventory()));
  assert(durable.offline.Commit(Inventory()));
  assert(session.identity.Finalize(View(), durable.online.Committed(), durable.offline.Committed(), true));
  assert(session.identity.Validate(View()) == JournalIdentityOwner::Validation::Ready);
  assert(session.journal.PersistReadyMask(63));
}

std::string Id(unsigned index) {
  const std::string digits = std::to_string(index);
  assert(digits.size() <= 8);
  return std::string(8 - digits.size(), '0') + digits + "-abcd-4abc-8abc-000000000000";
}

Identity Operation(unsigned index, uint64_t epoch = 1) {
  return {epoch, Id(index), static_cast<uint8_t>(index % 6 + 1), 1000 + index};
}

void Complete(Journal& journal, const Identity& identity, bool timed = false) {
  assert(journal.BeginScoped(identity.epoch, identity.operationId, identity.compartment, identity.fingerprint));
  assert(journal.ResolveRestored(identity.compartment));
  if (timed) assert(journal.ConfirmTimedPresentationScoped(identity.epoch, identity.operationId,
      identity.compartment, identity.fingerprint));
}

void Fill(Durable& durable) {
  Session session(durable);
  assert(durable.barrier.TryEnter());
  for (unsigned index = 0; index < Journal::HistoryCapacity; ++index) Complete(session.journal, Operation(index));
  durable.barrier.Leave();
}

bool Pinned(const Journal& journal, const Identity& identity) {
  return journal.IsEvidencePinned(identity.epoch, identity.operationId, identity.compartment, identity.fingerprint);
}

void AssertPresent(const Journal& journal, const Identity& identity) {
  assert(journal.FindHistorical(identity.epoch, identity.operationId,
      identity.compartment, identity.fingerprint) == CommandJournal::Lookup::Same);
}

void AssertReleased(const Durable& durable) { assert(!durable.barrier.held); }

void TestInvalidInventoriesDoNotMutatePins() {
  Durable durable;
  Provision(durable);
  Fill(durable);
  {
    Session session(durable);
    assert(session.journal.ReleaseEvidencePin(1, Id(0), 1, 1000));
  }
  assert(durable.online.Commit(Inventory({Operation(0)})));
  const Storage baseline = durable.journal;
  const auto valid = Inventory();
  std::vector<std::pair<JournalEvidenceInventory, Result>> invalid;
  auto value = valid; value.origin = JournalInventoryOrigin::Missing; invalid.push_back({value, Result::InventoryMissing});
  value = valid; value.origin = JournalInventoryOrigin::Unavailable; invalid.push_back({value, Result::InventoryUnavailable});
  value = valid; value.complete = false; invalid.push_back({value, Result::InventoryIncomplete});
  value = valid; value.ambiguous = true; invalid.push_back({value, Result::InventoryAmbiguous});
  value = valid; value.deviceId = "other-device"; invalid.push_back({value, Result::InventoryWrongDevice});
  value = valid; value.generation = 0; invalid.push_back({value, Result::InventoryInvalidGeneration});
  value = valid; value.count = JournalEvidenceInventory::Capacity + 1; invalid.push_back({value, Result::InventoryOverflow});
  value = Inventory({Operation(100)}); invalid.push_back({value, Result::ReferenceUnavailable});
  value = Inventory({Operation(0, 2)}); invalid.push_back({value, Result::InvalidReference});
  value = Inventory({Operation(0)}); value.references[0].operationId = "invalid"; invalid.push_back({value, Result::InvalidReference});
  value = Inventory({Operation(0)}); value.references[0].compartment = 7; invalid.push_back({value, Result::InvalidReference});
  value = Inventory({Operation(0)}); value.references[0].fingerprint = 0; invalid.push_back({value, Result::InvalidReference});
  value = Inventory({Operation(0)}); ++value.references[0].fingerprint; invalid.push_back({value, Result::ReferenceUnavailable});
  for (const auto& test : invalid) {
    assert(durable.offline.Commit(test.first));
    Session session(durable);
    const auto onlineReads = durable.online.reads, offlineReads = durable.offline.reads;
    assert(session.reconciliation.Reconcile(View()) == test.second);
    assert(durable.online.reads == onlineReads + 1 && durable.offline.reads == offlineReads + 1);
    assert(durable.journal.bytes == baseline.bytes && durable.journal.writes == baseline.writes);
    assert(!Pinned(session.journal, Operation(0))); // Online validation cannot re-pin before offline validates.
    AssertReleased(durable);
  }
  assert(durable.offline.Commit(valid));
  durable.offline.readable = false;
  Session unreadable(durable);
  assert(unreadable.reconciliation.Reconcile(View()) == Result::InventoryUnavailable);
  assert(durable.journal.bytes == baseline.bytes);
  AssertReleased(durable);
}

void TestRepairWithoutPrematureReleaseAndCaseIdentity() {
  Durable durable;
  Provision(durable);
  {
    Session session(durable);
    Complete(session.journal, Operation(0));
    Complete(session.journal, Operation(1));
    assert(session.journal.ReleaseEvidencePin(1, Id(0), 1, 1000));
  }
  auto upper = Operation(0);
  for (char& value : upper.operationId) if (value >= 'a' && value <= 'f') value = static_cast<char>(value - ('a' - 'A'));
  assert(durable.online.Commit(Inventory({upper, upper})));
  {
    Session session(durable);
    const auto writes = durable.journal.writes;
    assert(session.reconciliation.Reconcile(View()) == Result::Reconciled);
    assert(durable.journal.writes == writes + 1);
    assert(Pinned(session.journal, Operation(0)) && Pinned(session.journal, Operation(1)));
    assert(!session.journal.WasTimedPresentationCompletedHistorical(1, Id(0), 1, 1000));
    assert(session.journal.CurrentEpoch() == 1 && session.journal.HistoryCount() == 2);
    assert(session.reconciliation.Reconcile(View()) == Result::NotNeeded);
  }
  {
    Session session(durable);
    for (unsigned index = 2; index < Journal::HistoryCapacity; ++index) Complete(session.journal, Operation(index));
    assert(session.journal.ReleaseEvidencePin(1, Id(0), 1, 1000));
    const auto writes = durable.journal.writes;
    assert(session.reconciliation.Reconcile(View(), false) == Result::Reconciled);
    assert(durable.journal.writes == writes + 1 && session.journal.CurrentEpoch() == 1);
    for (unsigned index = 0; index < Journal::HistoryCapacity; ++index) assert(Pinned(session.journal, Operation(index)));
  }
  AssertReleased(durable);
}

void TestFullPinsPendingAndIdentityGuards() {
  Durable durable;
  Provision(durable);
  Fill(durable);
  std::vector<Identity> all;
  for (unsigned index = 0; index < Journal::HistoryCapacity; ++index) all.push_back(Operation(index));
  assert(durable.online.Commit(Inventory(all)));
  Session session(durable);
  assert(session.journal.ReleaseEvidencePin(1, Id(0), 1, 1000));
  const auto writes = durable.journal.writes;
  assert(session.reconciliation.Reconcile(View()) == Result::CapacityPinned);
  assert(durable.journal.writes == writes + 1 && Pinned(session.journal, Operation(0)));
  assert(session.journal.CurrentEpoch() == 1 && session.journal.HistoryCount() == Journal::HistoryCapacity);
  assert(durable.barrier.TryEnter());
  const auto reads = durable.online.reads;
  assert(session.reconciliation.Reconcile(View()) == Result::Busy);
  assert(durable.online.reads == reads);
  durable.barrier.Leave();
  auto wrong = View(); wrong.deviceId = "other-device";
  assert(session.reconciliation.Reconcile(wrong) == Result::IdentityNotReady);
  AssertReleased(durable);

  Durable pending;
  Provision(pending);
  Session active(pending);
  assert(active.journal.BeginScoped(1, Id(0), 1, 1000));
  const auto pendingBytes = pending.journal.bytes;
  const auto result = active.reconciliation.Reconcile(View());
  assert(result == Result::PendingOperation);
  assert(active.journal.HasPending() && pending.journal.bytes == pendingBytes);
  AssertReleased(pending);

  Durable missing;
  Session unestablished(missing);
  assert(unestablished.reconciliation.Reconcile(View()) == Result::IdentityNotReady);
  assert(missing.journal.writes == 0 && missing.marker.writes == 0);
  AssertReleased(missing);
}

void RefreshCrc(Storage& storage) {
  assert(storage.bytes.size() == Journal::RecordSize);
  uint32_t crc = 0xffffffffU;
  for (size_t index = 0; index < storage.bytes.size() - 4; ++index) {
    crc ^= storage.bytes[index];
    for (unsigned bit = 0; bit < 8; ++bit) crc = (crc >> 1) ^ ((crc & 1) ? 0xedb88320U : 0);
  }
  crc = ~crc;
  for (unsigned byte = 0; byte < 4; ++byte) storage.bytes[storage.bytes.size() - 4 + byte] = static_cast<uint8_t>(crc >> (8 * byte));
}

void TestEpochExhaustionHasNoDestructiveWrites() {
  Durable durable;
  Provision(durable);
  Fill(durable);
  for (unsigned byte = 0; byte < 8; ++byte) durable.journal.bytes[44 + byte] = 0xff;
  RefreshCrc(durable.journal);
  Session session(durable);
  assert(session.journal.CurrentEpoch() == std::numeric_limits<uint64_t>::max());
  const auto bytes = durable.journal.bytes;
  const auto writes = durable.journal.writes;
  assert(session.reconciliation.Reconcile(View()) == Result::EpochExhausted);
  assert(durable.journal.bytes == bytes && durable.journal.writes == writes);
  for (unsigned index = 0; index < Journal::HistoryCapacity; ++index) assert(Pinned(session.journal, Operation(index)));
  AssertReleased(durable);
}

void TestAtomicRepairAndCompactionWriteCuts() {
  Durable durable;
  Provision(durable);
  Fill(durable);
  {
    Session session(durable);
    assert(session.journal.ReleaseEvidencePin(1, Id(0), 1, 1000));
  }
  assert(durable.online.Commit(Inventory({Operation(0)})));
  assert(durable.offline.Commit(Inventory({Operation(1)})));
  const Storage baseline = durable.journal;
  const Storage marker = durable.marker;
  // Repair-only and repair+compaction each expose one atomic replacement.
  for (const bool compact : {false, true}) {
    for (const auto cut : {Storage::Cut::Before, Storage::Cut::After}) {
      durable.journal = baseline;
      durable.marker = marker;
      durable.journal.failAt = baseline.writes + 1;
      durable.journal.cut = cut;
      {
        Session session(durable);
        const Result result = session.reconciliation.Reconcile(View(), compact);
        assert(result == (compact ? Result::CompactionFailed : Result::PinRepairFailed));
        assert(!session.journal.IsHealthy() && session.journal.CurrentEpoch() != 2);
        assert(durable.journal.writes == baseline.writes + 1);
        const auto failedWrites = durable.journal.writes;
        assert(session.reconciliation.Reconcile(View()) != Result::Compacted);
        assert(durable.journal.writes == failedWrites);
        AssertReleased(durable);
      }
      durable.journal.cut = Storage::Cut::None;
      durable.journal.failAt = 0;
      {
        Session reopened(durable);
        AssertPresent(reopened.journal, Operation(0));
        AssertPresent(reopened.journal, Operation(1));
        const auto writes = durable.journal.writes;
        const auto result = reopened.reconciliation.Reconcile(View(), compact);
        assert(result == Result::Compacted || result == Result::Reconciled || result == Result::NotNeeded);
        assert(durable.journal.writes <= writes + 1);
        assert(reopened.journal.CurrentEpoch() == (compact ? 2u : 1u));
        assert(reopened.journal.HistoryCount() == (compact ? 2u : Journal::HistoryCapacity));
        assert(Pinned(reopened.journal, Operation(0)) && Pinned(reopened.journal, Operation(1)));
        assert(reopened.journal.FindScoped(1, Id(0), 1, 1000) ==
            (compact ? CommandJournal::Lookup::StaleEpoch : CommandJournal::Lookup::Same));
        assert(!reopened.journal.BeginScoped(1, Id(0), 1, 1000));
        AssertReleased(durable);
      }
    }
  }
}

void TestLiveReservationBlocksReconciliation() {
  Durable durable;
  Provision(durable);
  Session session(durable);
  const auto operation = Operation(0);
  assert(session.journal.ReservePresentation(operation.epoch, operation.operationId,
      operation.compartment, operation.fingerprint));
  const auto writes = durable.journal.writes;
  assert(!session.journal.HasPending());
  assert(session.reconciliation.Reconcile(View()) == Result::PresentationReserved);
  assert(durable.journal.writes == writes && Pinned(session.journal, operation));
  session.journal.CancelPresentationReservation();
  assert(session.reconciliation.Reconcile(View()) == Result::NotNeeded);
  assert(durable.journal.writes == writes && Pinned(session.journal, operation));
}

void TestBatchRejectsStaleSnapshotWithoutWriting() {
  Durable durable;
  Provision(durable);
  Fill(durable);
  Session session(durable);
  std::array<JournalHistoryEntry, Journal::HistoryCapacity> expected;
  std::array<bool, Journal::HistoryCapacity> referenced{};
  referenced[0] = true;
  for (size_t index = 0; index < expected.size(); ++index)
    assert(session.journal.ReadHistoryEntry(index, expected[index]));
  assert(durable.barrier.TryEnter());
  const auto writes = durable.journal.writes;
  assert(!session.journal.CommitEvidenceReconciliation(2, expected, expected.size(), referenced, true));
  assert(!session.journal.CommitEvidenceReconciliation(1, expected, expected.size() - 1, referenced, true));
  auto changed = expected;
  changed[0].identity.fingerprint++;
  assert(!session.journal.CommitEvidenceReconciliation(1, changed, changed.size(), referenced, true));
  changed = expected;
  changed[0].timedCompleted = !changed[0].timedCompleted;
  assert(!session.journal.CommitEvidenceReconciliation(1, changed, changed.size(), referenced, true));
  changed = expected;
  changed[0].evidencePinned = !changed[0].evidencePinned;
  assert(!session.journal.CommitEvidenceReconciliation(1, changed, changed.size(), referenced, true));
  assert(session.journal.IsHealthy() && durable.journal.writes == writes);
  assert(session.journal.CommitEvidenceReconciliation(1, expected, expected.size(), referenced, true));
  assert(durable.journal.writes == writes + 1 && session.journal.CurrentEpoch() == 2);
  assert(session.journal.HistoryCount() == 1 && Pinned(session.journal, Operation(0)));
  const auto committedWrites = durable.journal.writes;
  assert(!session.journal.CommitEvidenceReconciliation(1, expected, expected.size(), referenced, true));
  assert(durable.journal.writes == committedWrites && session.journal.IsHealthy());
  durable.barrier.Leave();
}

void TestCommittedSourcesAndBarrierInterleavings() {
  Durable durable;
  Provision(durable);
  Fill(durable);
  assert(durable.online.Commit(Inventory({Operation(0)})));
  durable.online.volatileDraft = Inventory(); // RAM retirement cannot authorize release.
  Session session(durable);
  unsigned blockedCommits = 0, blockedReads = 0, blockedReconciliations = 0;
  durable.online.onRead = [&]() {
    if (!durable.online.Commit(Inventory())) ++blockedCommits;
    if (!durable.barrier.TryEnter()) ++blockedReads;
    if (session.reconciliation.Reconcile(View()) == Result::Busy) ++blockedReconciliations;
  };
  durable.journal.onWrite = [&]() {
    if (!durable.offline.Commit(Inventory({Operation(1)}))) ++blockedCommits;
    if (!durable.barrier.TryEnter()) ++blockedReads;
  };
  assert(session.reconciliation.Reconcile(View()) == Result::Compacted);
  assert(blockedCommits == 2 && blockedReads == 2 && blockedReconciliations == 1);
  assert(durable.online.Committed().count == 1 && durable.offline.Committed().count == 0);
  assert(session.journal.HistoryCount() == 1 && Pinned(session.journal, Operation(0)));
  assert(!session.journal.WasTimedPresentationCompletedHistorical(1, Id(0), 1, 1000));
  AssertReleased(durable);
  // A late producer cannot substitute the current epoch for its captured one.
  assert(durable.barrier.TryEnter());
  assert(Operation(1).epoch != session.journal.CurrentEpoch());
  assert(!session.journal.BeginScoped(1, Id(1), 2, 1001));
  durable.barrier.Leave();
}

struct DelayedReference { Identity identity; unsigned retireAfter; };

void Publish(Evidence& source, const std::vector<DelayedReference>& references, Barrier& barrier) {
  std::vector<Identity> identities;
  for (const auto& reference : references) identities.push_back(reference.identity);
  assert(barrier.held);
  source.CommitWhileHeld(Inventory(identities, source.Committed().generation + 1));
}

void TestMixedOperationsAcrossRestartsAndCompactions() {
  Durable durable;
  Provision(durable);
  std::vector<DelayedReference> online, offline;
  std::vector<Identity> envelopes;
  unsigned compactions = 0;
  for (unsigned operation = 0; operation < MixedOperations; ++operation) {
    Identity captured;
    {
      Session session(durable);
      if (session.journal.HistoryCount() == Journal::HistoryCapacity) {
        assert(session.reconciliation.Reconcile(View()) == Result::Compacted);
        ++compactions;
      }
      // UUID reuse is deliberate across epochs; each new envelope captures the
      // authoritative current epoch before admission under the shared barrier.
      captured = Operation(operation % 64, session.journal.CurrentEpoch());
      captured.fingerprint = 10000 + operation;
      assert(durable.barrier.TryEnter());
      assert(session.journal.BeginScoped(captured.epoch, captured.operationId,
          captured.compartment, captured.fingerprint));
      durable.barrier.Leave();
    }
    {
      Session pending(durable);
      assert(pending.journal.HasPending());
      const auto bytes = durable.journal.bytes;
      assert(pending.reconciliation.Reconcile(View()) != Result::Compacted);
      assert(durable.journal.bytes == bytes);
      assert(durable.barrier.TryEnter());
      assert(pending.journal.ResolveRestored(captured.compartment));
      if (operation % 3) {
        assert(pending.journal.ConfirmTimedPresentationScoped(captured.epoch, captured.operationId,
            captured.compartment, captured.fingerprint));
        assert(pending.journal.RetainEvidencePin(captured.epoch, captured.operationId,
            captured.compartment, captured.fingerprint));
        (operation % 3 == 1 ? online : offline).push_back({captured, operation + 19});
      }
      const auto retired = [operation](const DelayedReference& reference) { return reference.retireAfter <= operation; };
      online.erase(std::remove_if(online.begin(), online.end(), retired), online.end());
      offline.erase(std::remove_if(offline.begin(), offline.end(), retired), offline.end());
      Publish(durable.online, online, durable.barrier);
      Publish(durable.offline, offline, durable.barrier);
      durable.barrier.Leave();
    }
    envelopes.push_back(captured);
    {
      Session reopened(durable);
      assert(reopened.reconciliation.Reconcile(View(), false) == Result::NotNeeded);
      for (const auto& reference : online) { AssertPresent(reopened.journal, reference.identity); assert(Pinned(reopened.journal, reference.identity)); }
      for (const auto& reference : offline) { AssertPresent(reopened.journal, reference.identity); assert(Pinned(reopened.journal, reference.identity)); }
      for (const auto& old : envelopes) {
        if (old.epoch == reopened.journal.CurrentEpoch()) continue;
        assert(reopened.journal.FindScoped(old.epoch, old.operationId,
            old.compartment, old.fingerprint) == CommandJournal::Lookup::StaleEpoch);
        assert(!reopened.journal.BeginScoped(old.epoch, old.operationId, old.compartment, old.fingerprint));
      }
    }
    AssertReleased(durable);
  }
  assert(envelopes.size() == MixedOperations && compactions > 30);
}

}  // namespace

int main() {
  TestInvalidInventoriesDoNotMutatePins();
  TestRepairWithoutPrematureReleaseAndCaseIdentity();
  TestFullPinsPendingAndIdentityGuards();
  TestEpochExhaustionHasNoDestructiveWrites();
  TestAtomicRepairAndCompactionWriteCuts();
  TestBatchRejectsStaleSnapshotWithoutWriting();
  TestLiveReservationBlocksReconciliation();
  TestCommittedSourcesAndBarrierInterleavings();
  TestMixedOperationsAcrossRestartsAndCompactions();
}

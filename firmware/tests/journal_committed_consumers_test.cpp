#include <algorithm>
#include <array>
#include <cassert>
#include <cstring>
#include <iostream>
#include <vector>

#include "../src/features/device_control/JournalIdentityOwner.h"
#include "../src/features/device_control/JournalReconciliation.h"
#include "../src/features/keepsake/KeepsakeCommittedInventory.h"
#include "../src/features/offline_show/OfflineCommittedInventory.h"

namespace {
using Journal = PersistentCommandJournal;
using Result = JournalReconciliation::Result;
using Offline = kufr::offline::OfflineCommittedInventory;
const std::string Device = "kufr-001";
const std::string Boot = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
const std::string Evening = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";

class Storage final : public JournalStorage {
 public:
  enum class Cut { None, Before, After };
  JournalReadResult Read(uint8_t* data, size_t capacity, size_t& length) override {
    length = 0;
    if (failRead || bytes.size() > capacity) return JournalReadResult::Failed;
    if (bytes.empty()) return JournalReadResult::Missing;
    std::copy(bytes.begin(), bytes.end(), data);
    length = bytes.size();
    return JournalReadResult::Found;
  }
  bool Write(const uint8_t* data, size_t length) override {
    ++writes;
    if (cut == Cut::Before) return false;
    bytes.assign(data, data + length);
    return cut != Cut::After;
  }
  std::vector<uint8_t> bytes;
  size_t writes = 0;
  bool failRead = false;
  Cut cut = Cut::None;
};

class Barrier final : public JournalReconciliationBarrier {
 public:
  bool TryEnter() override {
    if (held) return false;
    held = true;
    return true;
  }
  void Leave() override { assert(held); held = false; }
  bool held = false;
};

struct Durable {
  Storage journal, marker, online, offline;
  Barrier barrier;
};
struct Session {
  explicit Session(Durable& durable)
      : journal(durable.journal), identity(durable.marker, journal), online(durable.online),
        offline(durable.offline), reconciliation(journal, identity, online, offline, durable.barrier) {}
  Journal journal;
  JournalIdentityOwner identity;
  KeepsakeCommittedInventory online;
  Offline offline;
  JournalReconciliation reconciliation;
};

std::string Id(unsigned index) {
  const auto digits = std::to_string(index);
  return std::string(8 - digits.size(), '0') + digits + "-abcd-4abc-8abc-000000000000";
}
JournalIdentityView View() { return {true, true, Device}; }

void Provision(Durable& durable) {
  Session session(durable);
  const JournalIdentityView fresh{true, false, ""};
  assert(session.identity.BeginFactory(Device, fresh, true));
  assert(session.identity.ResumePreparation(fresh, true));
  assert(session.online.Establish(Device, true));
  assert(session.offline.EstablishBinding(Device, true));
  JournalEvidenceInventory online, offline;
  assert(session.online.ReadCommitted(online) && session.offline.ReadCommitted(offline));
  assert(session.identity.Finalize(View(), online, offline, true));
  assert(session.journal.PersistReadyMask(63));
}

void Complete(Journal& journal, const JournalOperationIdentity& operation, bool reserved = false) {
  assert(reserved
      ? journal.BeginReservedPresentation(operation.epoch, operation.operationId, operation.compartment, operation.fingerprint)
      : journal.BeginScoped(operation.epoch, operation.operationId, operation.compartment, operation.fingerprint));
  assert(journal.ResolveRestored(operation.compartment));
  assert(journal.ConfirmTimedPresentationScoped(operation.epoch, operation.operationId,
      operation.compartment, operation.fingerprint));
}

KeepsakeOutcome Candidate(const JournalOperationIdentity& operation, unsigned index) {
  KeepsakeOutcome result;
  result.operationId = Id(20000 + index);
  result.outcomeId = Id(40000 + index);
  result.proposalId = result.physicalOperationId = operation.operationId;
  result.bootId = Boot;
  result.generation = index + 1;
  result.deviceRevision = index + 2;
  result.compartment = operation.compartment;
  result.operationEpoch = operation.epoch;
  result.physicalFingerprint = operation.fingerprint;
  return result;
}

void Restore(KeepsakeCommittedInventory& committed, KeepsakeOutbox& box) {
  KeepsakeOutboxState state;
  assert(committed.ReadState(state));
  box.RetainRetirementLedger();
  assert(box.Restore(state.pending, state.quarantined, state.prepared));
}

void OnlineOperation(Session& session, const JournalOperationIdentity& operation, unsigned index) {
  KeepsakeOutbox box;
  Restore(session.online, box);
  for (const auto& previous : box.Snapshot()) {
    assert(box.RequestDeliveryAcknowledgement(previous));
    assert(box.ProcessDeliveryAcknowledgements([&](const KeepsakeOutboxState& next) {
      return session.online.Commit(next);
    }));
  }
  const auto candidate = Candidate(operation, index);
  assert(session.journal.ReservePresentation(operation.epoch, operation.operationId,
      operation.compartment, operation.fingerprint));
  assert(box.Prepare(candidate) && session.online.Commit(box.State()));
  Complete(session.journal, operation, true);
  assert(box.Queue(candidate) == KeepsakeQueueResult::VolatilePendingPersistence);
  assert(session.online.Commit(box.State()));
}

void OfflineOperation(Session& session, const JournalOperationIdentity& operation, unsigned index) {
  kufr::offline::Outbox box;
  assert(session.offline.Read(box));
  box.records[0] = {}; // Exact preceding ACK has been received by this test driver.
  assert(session.offline.Write(box));
  auto& record = box.records[0];
  std::copy(operation.operationId.begin(), operation.operationId.end(), record.operationId.begin());
  std::copy(Boot.begin(), Boot.end(), record.bootId.begin());
  std::copy(Evening.begin(), Evening.end(), record.localEveningId.begin());
  record.operationEpoch = operation.epoch;
  record.physicalFingerprint = operation.fingerprint;
  record.slot = operation.compartment;
  record.generation = index + 1;
  record.deviceRevision = index + 2;
  record.startedAtMs = index * 100;
  assert(session.journal.ReservePresentation(operation.epoch, operation.operationId,
      operation.compartment, operation.fingerprint));
  assert(session.offline.Write(box)); // Real reserved-record codec, before completion.
  Complete(session.journal, operation, true);
  record.completed = true;
  record.completedAtMs = record.startedAtMs + 50;
  assert(session.offline.Write(box));
}

void AssertRetained(Session& session) {
  JournalEvidenceInventory inventory;
  for (JournalEvidenceSource* source : std::array<JournalEvidenceSource*, 2>{{&session.online, &session.offline}}) {
    assert(source->ReadCommitted(inventory) && inventory.complete && !inventory.ambiguous);
    for (size_t index = 0; index < inventory.count; ++index) {
      const auto& identity = inventory.references[index];
      assert(session.journal.IsEvidencePinned(identity.epoch, identity.operationId,
          identity.compartment, identity.fingerprint));
      assert(session.journal.WasTimedPresentationCompletedHistorical(identity.epoch, identity.operationId,
          identity.compartment, identity.fingerprint));
    }
  }
}

void TestRealConsumersAcrossRestartsAndCompactions() {
  Durable durable;
  Provision(durable);
  unsigned compactions = 0;
  uint64_t lastEpoch = 1, onlineGeneration = 1, offlineGeneration = 1;
  for (unsigned index = 0; index < 1152; ++index) {
    Session session(durable); // Reopen all four actual codecs after every operation.
    assert(session.identity.Validate(View()) == JournalIdentityOwner::Validation::Ready);
    if (session.journal.HistoryCount() == Journal::HistoryCapacity) {
      const auto writes = durable.journal.writes;
      assert(session.reconciliation.Reconcile(View()) == Result::Compacted);
      assert(durable.journal.writes == writes + 1);
      ++compactions;
      assert(session.journal.CurrentEpoch() == ++lastEpoch);
      AssertRetained(session);
      assert(!session.journal.BeginScoped(lastEpoch - 1, Id(63), 1, 999));
    }
    assert(session.online.PruneRetired(session.journal, durable.barrier));
    assert(durable.barrier.TryEnter());
    const JournalOperationIdentity operation{session.journal.CurrentEpoch(), Id(index % 64),
        static_cast<uint8_t>(index % 6 + 1), index + uint64_t{1000}};
    if (index % 3 == 1) OnlineOperation(session, operation, index);
    else if (index % 3 == 2) OfflineOperation(session, operation, index);
    else Complete(session.journal, operation);
    durable.barrier.Leave();
    assert(session.reconciliation.Reconcile(View(), false) == Result::NotNeeded);
    JournalEvidenceInventory online, offline;
    assert(session.online.ReadCommitted(online) && session.offline.ReadCommitted(offline));
    assert(online.generation >= onlineGeneration && offline.generation >= offlineGeneration);
    onlineGeneration = online.generation;
    offlineGeneration = offline.generation;
    AssertRetained(session);
  }
  assert(compactions >= 35 && lastEpoch == compactions + 1);
}

void TestMissingAndFailedRealStoresBlockReclamation() {
  for (const bool missingOnline : {false, true}) {
    Durable durable;
    Provision(durable);
    Session seed(durable);
    for (unsigned index = 0; index < 32; ++index)
      Complete(seed.journal, {1, Id(index), 1, index + uint64_t{1000}});
    const auto original = durable.journal.bytes;
    (missingOnline ? durable.online : durable.offline).bytes.clear();
    Session missing(durable);
    assert(missing.reconciliation.Reconcile(View()) == Result::InventoryMissing);
    assert(durable.journal.bytes == original && missing.journal.CurrentEpoch() == 1);
    (missingOnline ? durable.online : durable.offline).failRead = true;
    Session failed(durable);
    assert(failed.reconciliation.Reconcile(View()) == Result::InventoryUnavailable);
    assert(durable.journal.bytes == original);
  }
}

void SeedFullConsumers(Durable& durable) {
  Provision(durable);
  Session session(durable);
  OnlineOperation(session, {1, Id(0), 1, 1000}, 0);
  OfflineOperation(session, {1, Id(1), 2, 1001}, 1);
  for (unsigned index = 2; index < 32; ++index)
    Complete(session.journal, {1, Id(index), 1, index + uint64_t{1000}});
}

void TestRealConsumersAtCompactionAndAcknowledgementCuts() {
  for (const auto cut : {Storage::Cut::Before, Storage::Cut::After}) {
    Durable durable;
    SeedFullConsumers(durable);
    const auto onlineBytes = durable.online.bytes;
    const auto offlineBytes = durable.offline.bytes;
    durable.journal.cut = cut;
    {
      Session interrupted(durable);
      assert(interrupted.reconciliation.Reconcile(View()) == Result::CompactionFailed);
      assert(!interrupted.journal.IsHealthy());
      assert(durable.online.bytes == onlineBytes && durable.offline.bytes == offlineBytes);
    }
    durable.journal.cut = Storage::Cut::None;
    Session reopened(durable);
    const auto result = reopened.reconciliation.Reconcile(View());
    assert(result == Result::Compacted || result == Result::NotNeeded);
    assert(reopened.journal.CurrentEpoch() == 2 && reopened.journal.HistoryCount() == 2);
    AssertRetained(reopened);
  }

  for (const auto cut : {Storage::Cut::Before, Storage::Cut::After}) {
    Durable durable;
    SeedFullConsumers(durable);
    {
      Session interrupted(durable);
      KeepsakeOutbox box;
      Restore(interrupted.online, box);
      assert(box.RequestDeliveryAcknowledgement(box.Snapshot().front()));
      durable.online.cut = cut;
      assert(!box.ProcessDeliveryAcknowledgements([&](const KeepsakeOutboxState& next) {
        return interrupted.online.Commit(next);
      }));
      const auto writes = durable.journal.writes;
      assert(interrupted.reconciliation.Reconcile(View()) == Result::InventoryUnavailable);
      assert(durable.journal.writes == writes); // RAM removal or a lost ACK is not a committed inventory.
    }
    durable.online.cut = Storage::Cut::None;
    Session reopened(durable);
    KeepsakeOutbox box;
    Restore(reopened.online, box); // Exact durable terminal fences win after a lost ACK.
    if (cut == Storage::Cut::Before) {
      assert(box.Snapshot().size() == 1);
      assert(box.RequestDeliveryAcknowledgement(box.Snapshot().front()));
      assert(box.ProcessDeliveryAcknowledgements([&](const KeepsakeOutboxState& next) {
        return reopened.online.Commit(next);
      }));
    } else {
      assert(box.Snapshot().empty());
      assert(reopened.online.Commit(box.State()));
    }
    assert(reopened.reconciliation.Reconcile(View()) == Result::Compacted);
    assert(reopened.journal.HistoryCount() == 1 && reopened.journal.CurrentEpoch() == 2);
    AssertRetained(reopened);
    assert(reopened.online.PruneRetired(reopened.journal, durable.barrier));
    JournalEvidenceInventory inventory;
    assert(reopened.online.ReadCommitted(inventory) && inventory.count == 0 && !inventory.ambiguous);
    assert(!reopened.journal.BeginScoped(1, Id(0), 1, 1000));
  }
}
}  // namespace

int main() {
  TestRealConsumersAcrossRestartsAndCompactions();
  TestMissingAndFailedRealStoresBlockReclamation();
  TestRealConsumersAtCompactionAndAcknowledgementCuts();
  std::cout << "PASS committed online/offline codecs: 1152 operations, restarts, retained proofs, commit cuts and missing-store fences\n";
}

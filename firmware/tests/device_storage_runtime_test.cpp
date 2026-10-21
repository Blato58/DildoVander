#include <algorithm>
#include <cassert>
#include <cstdio>
#include <functional>
#include <memory>
#include <vector>

#include "../src/features/device_control/DeviceStorageRuntime.h"
#include "../src/features/device_control/DeviceStorageStartup.h"
#include "../src/features/device_control/DeviceControlRuntime.h"

namespace {
class Storage final : public JournalStorage {
 public:
  JournalReadResult Read(uint8_t* output, size_t capacity, size_t& length) override {
    ++reads;
    length = bytes.size();
    if (failRead || length > capacity) return JournalReadResult::Failed;
    if (bytes.empty()) return JournalReadResult::Missing;
    std::copy(bytes.begin(), bytes.end(), output);
    return JournalReadResult::Found;
  }
  bool Write(const uint8_t* input, size_t length) override {
    ++writes;
    if (duringWrite) duringWrite();
    if (cutBefore) return false;
    bytes.assign(input, input + length);
    return !cutAfter;
  }
  std::vector<uint8_t> bytes;
  size_t writes = 0, reads = 0;
  bool failRead = false, cutBefore = false, cutAfter = false;
  std::function<void()> duringWrite;
};
JournalIdentityView Identity(bool provisioned = true) {
  JournalIdentityView view;
  view.healthy = true;
  view.provisioned = provisioned;
  if (provisioned) view.deviceId = "device-001";
  return view;
}
std::string Id(unsigned value) {
  char text[37];
  std::snprintf(text, sizeof(text), "aaaaaaaa-aaaa-4aaa-8aaa-%012x", value);
  return text;
}
struct Fixture {
  void Establish() {
    PersistentCommandJournal core(journal);
    JournalIdentityOwner identity(marker, core);
    KeepsakeCommittedInventory onlineOwner(online);
    kufr::offline::OfflineCommittedInventory offlineOwner(offline);
    assert(identity.BeginFactory("device-001", Identity(false), true));
    assert(core.EstablishScopedIdentity("device-001", true));
    assert(onlineOwner.Establish("device-001", true));
    assert(offlineOwner.EstablishBinding("device-001", true));
    JournalEvidenceInventory a, b;
    assert(onlineOwner.ReadCommitted(a) && offlineOwner.ReadCommitted(b));
    assert(identity.Finalize(Identity(), a, b, true));
    assert(core.PersistReadyMask(63)); // explicit fixture inventory loading, after service finalization
  }
  std::unique_ptr<DeviceStorageStartup> Start() {
    auto startup = std::make_unique<DeviceStorageStartup>(journal, marker, online, offline,
        Identity(), DeviceStorageRuntime::Policy::BoundedCompaction);
    assert(startup->Boot());
    assert(startup->CurrentState() == DeviceStorageStartup::State::Established);
    assert(startup->Runtime() && startup->ImageStorageHealthy() && !startup->RequiresService());
    assert(startup->ControlJournal().CurrentEpoch() == startup->Runtime()->ControlJournal().CurrentEpoch());
    return startup;
  }
  std::unique_ptr<DeviceStorageRuntime> Open() {
    return std::make_unique<DeviceStorageRuntime>(journal, marker, online, offline, Identity(),
        DeviceStorageRuntime::Policy::BoundedCompaction);
  }
  Storage journal, marker, online, offline;
};
class Safety final : public SafetyPort {
 public:
  void DisableActuators() override { ++disables; vibrating = false; }
  void StartVibration(uint8_t) override { assert(!moving()); ++vibrations; vibrating = true; }
  void ReleaseLatch(uint8_t) override { assert(!vibrating); ++moves; raised = true; dueAt = now + 100; }
  void LowerProp(uint8_t) override { assert(!vibrating); ++moves; raised = false; dueAt = now + 100; }
  bool PropRaised(uint8_t) const override { return raised && now >= dueAt; }
  bool PropResting(uint8_t) const override { return !raised && now >= dueAt; }
  uint32_t MotionTimeoutMs(uint8_t) const override { return 300; }
  bool moving() const { return now < dueAt; }
  size_t disables = 0, vibrations = 0, moves = 0;
  uint64_t now = 0, dueAt = 0;
  bool vibrating = false, raised = false;
};
struct Control {
  Control(DeviceStorageRuntime& storage, unsigned boot)
      : safety(port), sequence(safety, true),
        control("device-001", Id(boot), sequence, storage.ControlJournal(), 1),
        state(false, 0, ""), runtime(control, sequence, state, "catalog-1"), bootId(Id(boot)) {
    safety.InitializeSafe();
  }
  SelectionCommand Command(uint64_t now, unsigned id) {
    SelectionCommand command;
    command.deviceId = "device-001";
    command.bootId = bootId;
    command.controlLeaseId = "lease";
    command.commandId = Id(id);
    command.sequence = 1;
    command.operationEpoch = control.OperationEpoch();
    command.expectedRevision = control.Revision();
    command.issuedAtDeviceMs = now;
    command.maxAgeMs = 2000;
    command.compartment = 1;
    return command;
  }
  Safety port;
  SafetyController safety;
  SelectionSequence sequence;
  DeviceControl control;
  ConversationRuntimeState state;
  DeviceControlRuntime runtime;
  std::string bootId;
};
void MissingOrUnboundNeverCreatesAuthority() {
  Fixture missing;
  auto runtime = missing.Open();
  assert(!runtime->Boot() && !runtime->AllowsRecovery() && !runtime->AllowsAdmission());
  assert(!runtime->ControlJournal().BeginScoped(1, Id(1), 1, 77));
  assert(missing.journal.writes == 0 && missing.marker.writes == 0 &&
      missing.online.writes == 0 && missing.offline.writes == 0);
  for (unsigned lost = 0; lost < 4; ++lost) {
    Fixture fixture;
    fixture.Establish();
    Storage* stores[] = {&fixture.journal, &fixture.marker, &fixture.online, &fixture.offline};
    stores[lost]->bytes.clear();
    const auto journalWrites = fixture.journal.writes, onlineWrites = fixture.online.writes;
    auto storage = fixture.Open();
    assert(!storage->Boot() && !storage->AllowsAdmission());
    assert(!storage->AllowsRecovery());
    assert(fixture.journal.writes == journalWrites && fixture.online.writes == onlineWrites);
  }
}
void PendingBootRequiresInspectedReconciliation() {
  Fixture fixture;
  fixture.Establish();
  {
    PersistentCommandJournal core(fixture.journal);
    assert(core.BeginScoped(1, Id(7), 1, 77));
  }
  auto storage = fixture.Open();
  assert(!storage->Boot() && storage->AllowsRecovery() && !storage->AllowsAdmission());
  auto& journal = storage->ControlJournal();
  journal.BeginStorageRecovery();
  assert(!journal.Resolve(62));
  assert(storage->RunOne());
  assert(journal.Resolve(62));
  assert(!journal.ResumeStorageAfterRecovery()); // durable close is not complete-source reconciliation
  assert(storage->RunOne());
  assert(journal.ResumeStorageAfterRecovery());
  assert(storage->AllowsAdmission() && !journal.HasPending());
  assert(!journal.WasTimedPresentationCompletedHistorical(1, Id(7), 1, 77));
}
void LostBeginAcknowledgementsRemainUnknown() {
  for (bool after : {false, true}) {
    Fixture fixture;
    fixture.Establish();
    auto storage = fixture.Open();
    assert(storage->Boot());
    Control owner(*storage, 90);
    assert(owner.runtime.RecoverAfterInspection());
    assert(owner.runtime.AcquireManualControl("lease", 0));
    const auto command = owner.Command(0, 7);
    assert(owner.runtime.AcceptSelection(command, true, 0).deferred);
    fixture.journal.cutAfter = after;
    fixture.journal.cutBefore = !after;
    assert(storage->RunOne());
    const auto result = owner.runtime.AcceptSelection(command, true, 0);
    assert(!result.deferred && result.error != CommandError::None);
    assert(!owner.sequence.IsBusy() && !storage->AllowsAdmission());
    fixture.journal.cutAfter = fixture.journal.cutBefore = false;
    storage.reset();
    auto reopened = fixture.Open();
    const bool ready = reopened->Boot();
    assert(ready == !after);
    JournalSnapshot snapshot;
    assert(reopened->ReadJournalSnapshot(snapshot));
    assert((snapshot.pendingSlot != 0) == after);
  }
}
void SourceFailureStopsMotionAndDrainsReservedSlots() {
  for (bool whileMoving : {false, true}) {
    Fixture fixture;
    fixture.Establish();
    auto storage = fixture.Open();
    assert(storage->Boot());
    Control owner(*storage, 90);
    assert(owner.runtime.RecoverAfterInspection());
    assert(owner.runtime.AcquireManualControl("lease", 0));
    auto& journal = storage->ControlJournal();
    const auto command = owner.Command(0, 7);
    if (whileMoving) {
      assert(owner.runtime.AcceptSelection(command, true, 0).deferred);
      assert(storage->RunOne());
      assert(owner.runtime.AcceptSelection(command, true, 0).error == CommandError::None);
      SelectionStep step;
      assert(owner.runtime.BeginAcceptedSelection(0, step));
    } else {
      fixture.offline.duringWrite = [&]() {
        assert(!journal.BeginScoped(1, Id(9), 2, 77));
        assert(journal.LastStorageState() == CommandJournal::StorageState::Pending);
      };
    }
    EvidenceStorageOwner::Request request;
    request.operation = EvidenceStorageOwner::Operation::OfflineSettings;
    request.setQuiet = true; request.quiet = true;
    EvidenceStorageOwner::Token token;
    assert(storage->EvidenceRequests().TrySubmit(request, token));
    fixture.offline.cutAfter = true;
    assert(storage->RunOne());
    assert(!storage->AllowsRecovery() && journal.HasStorageFault());
    if (whileMoving) {
      const auto disables = owner.port.disables;
      SelectionStep step;
      assert(owner.runtime.Tick(10, step));
      assert(owner.port.disables > disables && !owner.sequence.IsBusy());
      assert(owner.control.LastResult().outcome == CommandOutcome::OutcomeUnknown);
    } else {
      journal.CancelStorageAdmissions();
      const auto writes = fixture.journal.writes;
      storage->RunOne(); // finish the reserved cancelled result without further storage access
      journal.PollStorage();
      assert(!journal.HasPendingStorage() && fixture.journal.writes == writes);
    }
    assert(!owner.runtime.RecoverAfterInspection());
    fixture.offline.duringWrite = {};
  }
}
void SeedRetiredLedger(Fixture& fixture) {
  // An explicit historical storage fixture, not a physical completion test.
  PersistentCommandJournal journal(fixture.journal);
  KeepsakeCommittedInventory online(fixture.online);
  KeepsakeOutboxState state;
  for (unsigned index = 0; index < 32; ++index) {
    const std::string physicalId = Id(2000 + index);
    const uint64_t fingerprint = 100 + index;
    assert(journal.BeginScoped(1, physicalId, 1, fingerprint));
    assert(journal.ResolveRestored(1));
    assert(journal.ConfirmTimedPresentationScoped(1, physicalId, 1, fingerprint));
    KeepsakeDisposition receipt;
    receipt.operationId = Id(3000 + index); receipt.outcomeId = Id(4000 + index);
    receipt.reason = "outcome_delivered"; receipt.operationEpoch = 1;
    receipt.physicalOperationId = physicalId; receipt.compartment = 1;
    receipt.physicalFingerprint = fingerprint;
    state.quarantined.push_back(receipt);
  }
  assert(online.Commit(state));
}
void PruningRecoversAcrossCommitAcknowledgementLoss() {
  for (bool after : {false, true}) {
    Fixture fixture;
    fixture.Establish();
    SeedRetiredLedger(fixture);
    auto storage = fixture.Open();
    assert(storage->Boot() && storage->Compactions() == 1);
    KeepsakeOutboxState state;
    uint64_t generation = 0;
    assert(storage->EvidenceRequests().TryReadOnline(state, &generation));
    assert(state.quarantined.size() == 32);
    fixture.online.cutBefore = !after; fixture.online.cutAfter = after;
    storage->RunOne();
    assert(!storage->AllowsAdmission() && !storage->AllowsRecovery());
    fixture.online.cutBefore = fixture.online.cutAfter = false;
    storage.reset();
    auto reopened = fixture.Open();
    assert(reopened->Boot());
    reopened->RunOne(); // cleanup retries even when the journal epoch was already committed
    uint64_t nextGeneration = 0;
    assert(reopened->EvidenceRequests().TryReadOnline(state, &nextGeneration));
    assert(state.quarantined.empty() && nextGeneration > generation);
    assert(reopened->ControlJournal().CurrentEpoch() == 2);
    assert(reopened->ControlJournal().FindHistorical(1, Id(2000), 1, 100) == CommandJournal::Lookup::Missing);
  }
}
void StartupRecoversOnlyOriginalProvenOutcomes() {
  for (bool after : {false, true}) {
    Fixture fixture;
    fixture.Establish();
    KeepsakeOutcome original;
    original.operationId = Id(100); original.outcomeId = Id(101);
    original.proposalId = original.physicalOperationId = Id(102);
    original.bootId = Id(103); original.compartment = 1;
    original.operationEpoch = 1; original.physicalFingerprint = 77;
    {
      PersistentCommandJournal journal(fixture.journal);
      assert(journal.ReservePresentation(1, original.physicalOperationId, 1, 77));
      KeepsakeCommittedInventory online(fixture.online);
      KeepsakeOutboxState state;
      state.prepared.push_back(original);
      assert(online.Commit(state));
      assert(journal.BeginReservedPresentation(1, original.physicalOperationId, 1, 77));
      assert(journal.ResolveRestored(1));
      assert(journal.ConfirmTimedPresentationScoped(1, original.physicalOperationId, 1, 77));
    }
    fixture.online.cutBefore = !after; fixture.online.cutAfter = after;
    auto failed = fixture.Open();
    assert(!failed->Boot() && !failed->AllowsAdmission());
    fixture.online.cutBefore = fixture.online.cutAfter = false;
    failed.reset();
    auto recovered = fixture.Open();
    assert(recovered->Boot());
    KeepsakeOutboxState state;
    assert(recovered->EvidenceRequests().TryReadOnline(state));
    assert(state.prepared.empty() && state.pending.size() == 1);
    assert(SameKeepsakeCandidate(state.pending[0], original));
    assert(state.pending[0].operationEpoch == 1 && recovered->ControlJournal().CurrentEpoch() == 1);
  }
}
void ControlLifecycleAcrossCompactionsAndRestarts() {
  Fixture fixture;
  fixture.Establish();
  uint64_t time = 0, compactions = 0, firstFingerprint = 0;
  SelectionCommand first;
  for (unsigned operation = 0; operation < 1152; ++operation) {
    auto startup = fixture.Start();
    auto* storage = startup->Runtime();
    Control owner(*storage, operation + 100000);
    assert(owner.runtime.RecoverAfterInspection());
    assert(owner.runtime.AcquireManualControl("lease", time));
    auto command = owner.Command(time, operation + 1);
    const auto before = owner.runtime.AcceptSelection(command, true, time);
    assert(before.deferred && !owner.sequence.IsBusy());
    assert(storage->RunOne());
    const auto accepted = owner.runtime.AcceptSelection(command, true, time);
    assert(!accepted.deferred && accepted.error == CommandError::None);
    if (operation == 0) {
      first = command;
      JournalSnapshot snapshot;
      assert(storage->ReadJournalSnapshot(snapshot));
      firstFingerprint = snapshot.history[0].fingerprint;
      KeepsakeOutcome candidate;
      candidate.operationId = Id(90000); candidate.outcomeId = Id(90001);
      candidate.proposalId = candidate.physicalOperationId = command.commandId;
      candidate.bootId = command.bootId; candidate.generation = 1;
      candidate.deviceRevision = command.expectedRevision;
      candidate.compartment = command.compartment;
      candidate.operationEpoch = command.operationEpoch;
      candidate.physicalFingerprint = firstFingerprint;
      EvidenceStorageOwner::Request request;
      EvidenceStorageOwner::Token token;
      assert(EvidenceStorageOwner::MakeOnlineRequest(EvidenceStorageOwner::Operation::OnlinePrepare, candidate, request));
      assert(storage->EvidenceRequests().TrySubmit(request, token));
      assert(storage->RunOne());
      EvidenceStorageOwner::Completion completion;
      assert(storage->EvidenceRequests().TryReadCompletion(token, completion));
      assert(completion.status == EvidenceStorageOwner::Status::Succeeded);
      assert(storage->EvidenceRequests().ReleaseCompletion(token));
    }
    SelectionStep step;
    // This lower-level runtime workload begins the accepted intent directly.
    // Actual transport ACK gating is exercised by offline_local_control_owner_test.
    owner.port.now = time;
    assert(owner.runtime.BeginAcceptedSelection(time, step));
    const uint64_t started = time;
    while (owner.control.HasPendingSelection()) {
      time += 10;
      owner.port.now = time;
      assert(time - started < 15000);
      if ((time - started) % 1000 == 0) assert(owner.runtime.RenewManualLease("lease", time));
      owner.runtime.Tick(time, step);
      storage->RunOne();
    }
    if (owner.control.LastResult().outcome != CommandOutcome::Completed) {
      std::printf("failed operation=%u elapsed=%llu state=%u recovery=%u storageFault=%u effect=%u\n",
          operation, static_cast<unsigned long long>(time - started),
          static_cast<unsigned>(storage->CurrentState()), owner.control.NeedsRecovery(),
          storage->ControlJournal().HasStorageFault(), static_cast<unsigned>(step.effect));
      std::fflush(stdout);
    }
    assert(owner.control.LastResult().outcome == CommandOutcome::Completed);
    assert(owner.port.moves != 0 && owner.port.vibrations != 0 && !owner.sequence.IsBusy());
    storage->RunOne(); // quiescent pin reconciliation/optional compaction after result adoption
    compactions += storage->Compactions();
    if (operation > 64) {
      auto replay = first;
      assert(owner.runtime.AcceptSelection(replay, true, time).error == CommandError::WrongBoot);
      replay.bootId = owner.bootId; // an attacker changing transport boot cannot revive the old epoch
      assert(owner.runtime.AcceptSelection(replay, true, time).error == CommandError::WrongOperationEpoch);
      assert(storage->ControlJournal().FindHistorical(1, first.commandId, 1, firstFingerprint) == CommandJournal::Lookup::Same);
      assert(!storage->ControlJournal().WasTimedPresentationCompletedHistorical(1, first.commandId, 1, firstFingerprint));
    }
  }
  assert(compactions >= 30);
  auto finalStartup = fixture.Start();
  auto* finalStorage = finalStartup->Runtime();
  KeepsakeOutboxState retained;
  assert(finalStorage->EvidenceRequests().TryReadOnline(retained));
  assert(retained.prepared.size() == 1 && retained.prepared[0].physicalFingerprint == firstFingerprint);
  std::printf("PASS 1152 manual runtime cycles via production startup, boots=1152 verificationBoots=1, %llu compactions, restarts and retained untimed evidence; simulated time %llu ms\n",
      static_cast<unsigned long long>(compactions), static_cast<unsigned long long>(time));
}
} // namespace
int main() {
  MissingOrUnboundNeverCreatesAuthority();
  PendingBootRequiresInspectedReconciliation();
  LostBeginAcknowledgementsRemainUnknown();
  SourceFailureStopsMotionAndDrainsReservedSlots();
  PruningRecoversAcrossCommitAcknowledgementLoss();
  StartupRecoversOnlyOriginalProvenOutcomes();
  ControlLifecycleAcrossCompactionsAndRestarts();
}

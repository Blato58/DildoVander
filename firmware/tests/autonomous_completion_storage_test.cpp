#include <algorithm>
#include <cassert>
#include <memory>
#include <vector>

#include "../src/features/device_control/DeviceControlRuntime.h"
#include "../src/features/device_control/DeviceStorageRuntime.h"
#include "../src/features/wand_presentation/WandPresentation.h"

namespace {
constexpr char OperationId[] = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
constexpr uint8_t Slot = 2;
constexpr uint64_t Fingerprint = 77;
using Progress = DeviceControlRuntime::PresentationProgress;
using Evidence = EvidenceStorageOwner;

class MemoryStorage final : public JournalStorage {
 public:
  JournalReadResult Read(uint8_t* output, size_t capacity, size_t& length) override {
    length = bytes.size();
    if (length > capacity) return JournalReadResult::Failed;
    if (bytes.empty()) return JournalReadResult::Missing;
    std::copy(bytes.begin(), bytes.end(), output);
    return JournalReadResult::Found;
  }
  bool Write(const uint8_t* input, size_t length) override {
    ++writes;
    if (cutBefore) return false;
    bytes.assign(input, input + length);
    return !cutAfter;
  }
  std::vector<uint8_t> bytes;
  unsigned writes = 0;
  bool cutBefore = false, cutAfter = false;
};

JournalIdentityView Identity(bool provisioned = true) {
  JournalIdentityView identity;
  identity.healthy = true;
  identity.provisioned = provisioned;
  if (provisioned) identity.deviceId = "completion-device";
  return identity;
}

struct DurableFixture {
  DurableFixture() {
    PersistentCommandJournal core(journal);
    JournalIdentityOwner identity(marker, core);
    KeepsakeCommittedInventory onlineOwner(online);
    kufr::offline::OfflineCommittedInventory offlineOwner(offline);
    assert(identity.BeginFactory("completion-device", Identity(false), true));
    assert(core.EstablishScopedIdentity("completion-device", true));
    assert(onlineOwner.Establish("completion-device", true));
    assert(offlineOwner.EstablishBinding("completion-device", true));
    JournalEvidenceInventory onlineInventory, offlineInventory;
    assert(onlineOwner.ReadCommitted(onlineInventory));
    assert(offlineOwner.ReadCommitted(offlineInventory));
    assert(identity.Finalize(Identity(), onlineInventory, offlineInventory, true));
    assert(core.PersistReadyMask(63)); // Explicit inspected fixture loading.
  }
  std::unique_ptr<DeviceStorageRuntime> Open() {
    return std::make_unique<DeviceStorageRuntime>(journal, marker, online, offline,
        Identity(), DeviceStorageRuntime::Policy::BoundedCompaction);
  }
  MemoryStorage journal, marker, online, offline;
};

// TEST ONLY: current production admission still expects synchronous bools.
// Pump the real owner only for Reserve/Begin setup. Close/proof and recovery
// forward directly, preserving their real mailbox, cancellation and ACK waits.
class AdmissionSetupBridge final : public CommandJournal {
 public:
  explicit AdmissionSetupBridge(DeviceStorageRuntime& storage)
      : storage_(storage), journal_(storage.ControlJournal()) {}
  StorageState LastStorageState() const override { return journal_.LastStorageState(); }
  bool HasPendingStorage() const override { return journal_.HasPendingStorage(); }
  bool HasStorageFault() const override { return journal_.HasStorageFault(); }
  void PollStorage() override { journal_.PollStorage(); }
  void CancelStorageAdmissions() override { journal_.CancelStorageAdmissions(); }
  void BeginStorageRecovery() override { journal_.BeginStorageRecovery(); }
  bool ResumeStorageAfterRecovery() override { return journal_.ResumeStorageAfterRecovery(); }
  bool HasPending() const override { return journal_.HasPending(); }
  bool CanBeginOperation() const override { return journal_.CanBeginOperation(); }
  uint8_t PendingCompartment() const override { return journal_.PendingCompartment(); }
  uint8_t ReadyMask() const override { return journal_.ReadyMask(); }
  uint64_t CurrentEpoch() const override { return journal_.CurrentEpoch(); }
  bool Begin(const std::string& id, uint8_t slot) override { return journal_.Begin(id, slot); }
  bool Resolve(uint8_t mask) override { return journal_.Resolve(mask); }
  bool PersistReadyMask(uint8_t mask) override { return journal_.PersistReadyMask(mask); }
  bool ResolveRestored(uint8_t slot) override { return journal_.ResolveRestored(slot); }
  bool ReservePresentation(uint64_t epoch, const std::string& id, uint8_t slot,
      uint64_t fingerprint) override {
    if (journal_.ReservePresentation(epoch, id, slot, fingerprint)) return true;
    if (LastStorageState() != StorageState::Pending) return false;
    assert(storage_.RunOne());
    return journal_.ReservePresentation(epoch, id, slot, fingerprint);
  }
  bool BeginReservedPresentation(uint64_t epoch, const std::string& id, uint8_t slot,
      uint64_t fingerprint) override {
    if (journal_.BeginReservedPresentation(epoch, id, slot, fingerprint)) return true;
    if (LastStorageState() != StorageState::Pending) return false;
    assert(storage_.RunOne());
    return journal_.BeginReservedPresentation(epoch, id, slot, fingerprint);
  }
  bool HasPresentationReservation() const override { return journal_.HasPresentationReservation(); }
  bool HasPresentationReservation(uint64_t epoch, const std::string& id, uint8_t slot,
      uint64_t fingerprint) const override {
    return journal_.HasPresentationReservation(epoch, id, slot, fingerprint);
  }
  void CancelPresentationReservation() override { journal_.CancelPresentationReservation(); }
  Lookup FindScoped(uint64_t epoch, const std::string& id, uint8_t slot,
      uint64_t fingerprint) const override { return journal_.FindScoped(epoch, id, slot, fingerprint); }
  Lookup FindHistorical(uint64_t epoch, const std::string& id, uint8_t slot,
      uint64_t fingerprint) const override { return journal_.FindHistorical(epoch, id, slot, fingerprint); }
  bool ConfirmTimedPresentationScoped(uint64_t epoch, const std::string& id, uint8_t slot,
      uint64_t fingerprint) override {
    return journal_.ConfirmTimedPresentationScoped(epoch, id, slot, fingerprint);
  }
  bool WasTimedPresentationCompletedScoped(uint64_t epoch, const std::string& id,
      uint8_t slot, uint64_t fingerprint) const override {
    return journal_.WasTimedPresentationCompletedScoped(epoch, id, slot, fingerprint);
  }
  bool WasTimedPresentationCompletedHistorical(uint64_t epoch, const std::string& id,
      uint8_t slot, uint64_t fingerprint) const override {
    return journal_.WasTimedPresentationCompletedHistorical(epoch, id, slot, fingerprint);
  }
 private:
  DeviceStorageRuntime& storage_;
  CommandJournal& journal_;
};

class Safety final : public SafetyPort {
 public:
  void DisableActuators() override {}
  void StartVibration(uint8_t) override { assert(false); }
  void ReleaseLatch(uint8_t) override { assert(false); }
};
class IdlePort final : public WandPresentationPort {
 public:
  bool IsReady(uint8_t) const override { return true; }
  bool IsSafeToContinue() const override { return true; }
  bool IsRaised(uint8_t) const override { return false; }
  bool IsResting(uint8_t) const override { return true; }
  bool Raise(uint8_t, const std::string&, const ConversationRuntimeSnapshot&) override { assert(false); return false; }
  bool Lower(uint8_t) override { assert(false); return false; }
  bool Complete(uint8_t) override { assert(false); return false; }
  void DisableAndFault() override {}
};

Evidence::Completion RunEvidence(DeviceStorageRuntime& storage, Evidence::Operation operation,
    const KeepsakeOutcome& outcome) {
  Evidence::Request request;
  assert(Evidence::MakeOnlineRequest(operation, outcome, request));
  Evidence::Token token;
  assert(storage.EvidenceRequests().TrySubmit(request, token));
  assert(storage.RunOne());
  Evidence::Completion completion;
  assert(storage.EvidenceRequests().TryReadCompletion(token, completion));
  assert(storage.EvidenceRequests().ReleaseCompletion(token));
  return completion;
}

void AssertSource(DeviceStorageRuntime& storage, size_t prepared, size_t pending) {
  KeepsakeOutboxState source;
  assert(storage.EvidenceRequests().TryReadOnline(source));
  assert(source.prepared.size() == prepared && source.pending.size() == pending);
  if (prepared != 0) {
    assert(source.prepared[0].physicalOperationId == OperationId);
    assert(source.prepared[0].compartment == Slot);
    assert(source.prepared[0].physicalFingerprint == Fingerprint);
  }
}

struct Control {
  explicit Control(DeviceStorageRuntime& storage) : storage(storage), bridge(storage),
      safety(safetyPort), selection(safety), control("completion-device",
      "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", selection, bridge, 1),
      state(false, 0, ""), runtime(control, selection, state, "catalog-1"), presentation(idlePort) {
    safety.InitializeSafe();
    assert(runtime.BindPresentation(presentation));
    Recover();
  }
  void Recover() {
    for (unsigned attempt = 0; attempt != 8; ++attempt) {
      if (runtime.RecoverAfterInspection()) return;
      storage.RunOne();
    }
    assert(false);
  }
  void Begin() {
    ConversationRuntimeSnapshot snapshot;
    assert(state.Read(snapshot));
    epoch = snapshot.operationEpoch;
    assert(runtime.ReserveAutonomousPresentation(OperationId, Slot, snapshot, Fingerprint));
    assert(bridge.HasPresentationReservation(epoch, OperationId, Slot, Fingerprint));
    assert(!bridge.HasPresentationReservation(epoch, OperationId, Slot, Fingerprint + 1));
    outcome.operationId = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    outcome.outcomeId = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";
    outcome.proposalId = outcome.physicalOperationId = OperationId;
    outcome.bootId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    outcome.generation = snapshot.generation;
    outcome.deviceRevision = snapshot.deviceRevision;
    outcome.compartment = Slot;
    outcome.operationEpoch = epoch;
    outcome.physicalFingerprint = Fingerprint;
    assert(RunEvidence(storage, Evidence::Operation::OnlinePrepare, outcome).status == Evidence::Status::Succeeded);
    assert(runtime.BeginAutonomousPresentation(OperationId, Slot, snapshot, Fingerprint));
    assert(!bridge.HasPresentationReservation());
    assert(bridge.FindScoped(epoch, OperationId, Slot, Fingerprint) == CommandJournal::Lookup::Same);
    assert(bridge.FindScoped(epoch, OperationId, Slot, Fingerprint + 1) == CommandJournal::Lookup::Conflict);
    AssertSource(storage, 1, 0);
  }
  static bool Check(void* context) {
    auto& owner = *static_cast<Control*>(context);
    assert(!owner.bridge.HasPending());
    assert(!owner.bridge.HasPendingStorage()); // Original close ACK was consumed.
    ++owner.checks;
    return owner.timely;
  }
  Progress Advance() { return runtime.AdvanceAutonomousCompletion(Slot, Check, this); }
  void Stop() { SelectionStep step; runtime.Stop(step); }
  bool Proof() const {
    return bridge.WasTimedPresentationCompletedHistorical(epoch, OperationId, Slot, Fingerprint);
  }
  DeviceStorageRuntime& storage;
  AdmissionSetupBridge bridge;
  Safety safetyPort;
  SafetyController safety;
  SelectionSequence selection;
  DeviceControl control;
  ConversationRuntimeState state;
  DeviceControlRuntime runtime;
  IdlePort idlePort;
  WandPresentation presentation;
  KeepsakeOutcome outcome;
  uint64_t epoch = 0;
  unsigned checks = 0;
  bool timely = true;
};

void CloseThenProofAndExactSource() {
  DurableFixture durable;
  auto storage = durable.Open();
  assert(storage->Boot());
  Control owner(*storage);
  owner.Begin();
  assert(RunEvidence(*storage, Evidence::Operation::OnlineComplete, owner.outcome).status == Evidence::Status::Refused);
  assert(owner.Advance() == Progress::Pending && owner.checks == 0);
  assert(owner.runtime.AdvanceAutonomousCompletion(Slot + 1, Control::Check,
      &owner) == Progress::Rejected);
  assert(owner.Advance() == Progress::Pending && owner.checks == 0);
  assert(storage->RunOne());
  owner.bridge.PollStorage(); // Adopt close before original caller consumes ACK.
  assert(!owner.bridge.HasPending() && owner.bridge.HasPendingStorage());
  assert(owner.Advance() == Progress::Pending && owner.checks == 1);
  assert(owner.runtime.HasValidatedAutonomousCompletion() && !owner.Proof());
  AssertSource(*storage, 1, 0);
  // This fixture's producer submits OnlineComplete only after Runtime success.
  // The raw evidence owner accepts historical proof, independently of live ACK.
  owner.timely = false; // Proof ACK delay never changes the completed timing check.
  assert(storage->RunOne());
  owner.bridge.PollStorage();
  assert(owner.Proof() && owner.bridge.HasPendingStorage());
  AssertSource(*storage, 1, 0);
  assert(owner.Advance() == Progress::Succeeded && owner.checks == 1);
  assert(!owner.bridge.HasPendingStorage());
  auto wrong = owner.outcome;
  wrong.physicalFingerprint += 1;
  assert(RunEvidence(*storage, Evidence::Operation::OnlineComplete, wrong).status == Evidence::Status::Refused);
  wrong = owner.outcome;
  wrong.compartment += 1;
  assert(RunEvidence(*storage, Evidence::Operation::OnlineComplete, wrong).status == Evidence::Status::Refused);
  AssertSource(*storage, 1, 0);
  assert(RunEvidence(*storage, Evidence::Operation::OnlineComplete, owner.outcome).status == Evidence::Status::Succeeded);
  AssertSource(*storage, 0, 1);
  assert(owner.Advance() == Progress::Rejected);
}

void StopAtCloseAndProofBoundaries() {
  for (bool sealed : {false, true}) {
    for (bool workerBeforeStop : {false, true}) {
      DurableFixture durable;
      auto storage = durable.Open();
      assert(storage->Boot());
      Control owner(*storage);
      owner.Begin();
      assert(owner.Advance() == Progress::Pending);
      if (sealed) {
        assert(storage->RunOne());
        assert(owner.Advance() == Progress::Pending);
        assert(owner.runtime.HasValidatedAutonomousCompletion());
      }
      if (workerBeforeStop) assert(storage->RunOne());
      owner.Stop();
      assert(!owner.runtime.HasValidatedAutonomousCompletion());
      if (!workerBeforeStop) assert(storage->RunOne());
      owner.bridge.PollStorage(); // Cancelled results are adopted and released.
      assert(!owner.bridge.HasPendingStorage() && !owner.bridge.HasPending());
      assert(owner.Proof() == sealed);
      assert(owner.checks == (sealed ? 1u : 0u));
      assert(owner.Advance() == Progress::Rejected);
      AssertSource(*storage, 1, 0);
      owner.Recover();
      assert(!owner.runtime.HasValidatedAutonomousCompletion());
      assert(owner.Advance() == Progress::Rejected);
    }
  }
}

void LateCloseCheckNeverCreatesProof() {
  DurableFixture durable;
  auto storage = durable.Open();
  assert(storage->Boot());
  Control owner(*storage);
  owner.Begin();
  assert(owner.Advance() == Progress::Pending);
  assert(storage->RunOne());
  owner.timely = false;
  assert(owner.Advance() == Progress::Rejected && owner.checks == 1);
  assert(owner.runtime.NeedsRecovery());
  assert(!owner.runtime.HasValidatedAutonomousCompletion());
  assert(!owner.bridge.HasPendingStorage() && !owner.Proof());
  AssertSource(*storage, 1, 0);
  owner.Recover();
  assert(!owner.Proof() && owner.Advance() == Progress::Rejected);
}

void JournalAckCutsAndReopen() {
  for (bool proofPhase : {false, true}) {
    for (bool afterWrite : {false, true}) {
      DurableFixture durable;
      auto storage = durable.Open();
      assert(storage->Boot());
      {
        Control owner(*storage);
        owner.Begin();
        assert(owner.Advance() == Progress::Pending);
        if (proofPhase) {
          assert(storage->RunOne());
          assert(owner.Advance() == Progress::Pending);
          assert(owner.runtime.HasValidatedAutonomousCompletion());
        }
        durable.journal.cutBefore = !afterWrite;
        durable.journal.cutAfter = afterWrite;
        assert(storage->RunOne());
        assert(owner.Advance() == Progress::Rejected);
        assert(owner.runtime.NeedsRecovery());
        assert(!owner.runtime.HasValidatedAutonomousCompletion());
        owner.bridge.PollStorage();
        assert(!owner.bridge.HasPendingStorage());
        assert(owner.checks == (proofPhase ? 1u : 0u));
        AssertSource(*storage, 1, 0);
      }
      storage.reset();
      durable.journal.cutBefore = durable.journal.cutAfter = false;
      auto reopened = durable.Open();
      const bool booted = reopened->Boot();
      assert(booted == (proofPhase || afterWrite));
      const bool durableProof = proofPhase && afterWrite;
      auto& journal = reopened->ControlJournal();
      journal.PollStorage();
      assert(journal.WasTimedPresentationCompletedHistorical(1, OperationId, Slot, Fingerprint) == durableProof);
      assert(!journal.WasTimedPresentationCompletedHistorical(1, OperationId, Slot, Fingerprint + 1));
      assert(!journal.WasTimedPresentationCompletedHistorical(1, OperationId, Slot + 1, Fingerprint));
      AssertSource(*reopened, durableProof ? 0u : 1u, durableProof ? 1u : 0u);
      // Inspection may resolve uncertain open Close; recovery never adds proof.
      Control recovered(*reopened);
      recovered.epoch = 1;
      assert(recovered.Advance() == Progress::Rejected);
      assert(!recovered.runtime.HasValidatedAutonomousCompletion());
      assert(recovered.Proof() == durableProof);
    }
  }
}
} // namespace

int main() {
  CloseThenProofAndExactSource();
  StopAtCloseAndProofBoundaries();
  LateCloseCheckNeverCreatesProof();
  JournalAckCutsAndReopen();
}

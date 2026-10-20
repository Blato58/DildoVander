#include <algorithm>
#include <cassert>
#include <cstdio>
#include <cstring>
#include <memory>
#include <vector>

#include "../src/features/device_control/DeviceStorageRuntime.h"
#include "../src/features/device_control/DeviceStorageStartup.h"
#include "physical_effects_test_support.h"

namespace {
using Evidence = EvidenceStorageOwner;
constexpr uint8_t Slot = 3;

std::string Id(unsigned value) {
  char text[37];
  std::snprintf(text, sizeof(text), "aaaaaaaa-aaaa-4aaa-8aaa-%012x", value);
  return text;
}

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
  JournalIdentityView value;
  value.healthy = true;
  value.provisioned = provisioned;
  if (provisioned) value.deviceId = "claimed-device";
  return value;
}

struct DurableFixture {
  DurableFixture() {
    PersistentCommandJournal journalOwner(journal);
    JournalIdentityOwner identity(marker, journalOwner);
    KeepsakeCommittedInventory onlineOwner(online);
    kufr::offline::OfflineCommittedInventory offlineOwner(offline);
    assert(identity.BeginFactory("claimed-device", Identity(false), true));
    assert(journalOwner.EstablishScopedIdentity("claimed-device", true));
    assert(onlineOwner.Establish("claimed-device", true));
    assert(offlineOwner.EstablishBinding("claimed-device", true));
    JournalEvidenceInventory onlineInventory, offlineInventory;
    assert(onlineOwner.ReadCommitted(onlineInventory));
    assert(offlineOwner.ReadCommitted(offlineInventory));
    assert(identity.Finalize(Identity(), onlineInventory, offlineInventory, true));
    assert(journalOwner.PersistReadyMask(63));
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
    return std::make_unique<DeviceStorageRuntime>(journal, marker, online, offline,
        Identity(), DeviceStorageRuntime::Policy::BoundedCompaction);
  }
  MemoryStorage journal, marker, online, offline;
};

Evidence::Token Submit(DeviceStorageRuntime& storage, Evidence::Operation operation,
    const KeepsakeOutcome& outcome) {
  Evidence::Request request;
  Evidence::Token token;
  assert(Evidence::MakeOnlineRequest(operation, outcome, request));
  assert(storage.EvidenceRequests().TrySubmit(request, token));
  return token;
}

Evidence::Completion Consume(DeviceStorageRuntime& storage, const Evidence::Token& token) {
  Evidence::Completion completion;
  assert(storage.EvidenceRequests().TryReadCompletion(token, completion));
  assert(storage.EvidenceRequests().ReleaseCompletion(token));
  return completion;
}

Evidence::Completion RunEvidence(DeviceStorageRuntime& storage, Evidence::Operation operation,
    const KeepsakeOutcome& outcome) {
  const auto token = Submit(storage, operation, outcome);
  assert(storage.RunOne());
  return Consume(storage, token);
}

bool Contains(const std::vector<KeepsakeOutcome>& items, const KeepsakeOutcome& outcome) {
  for (const auto& item : items) if (SameKeepsakeCandidate(item, outcome)) return true;
  return false;
}

KeepsakeOutboxState Source(DeviceStorageRuntime& storage) {
  KeepsakeOutboxState value;
  assert(storage.EvidenceRequests().TryReadOnline(value));
  return value;
}

// There is no admission bridge here: every journal and source request is
// queued, executed by the real worker, and consumed by its original caller.
struct Rig {
  explicit Rig(DeviceStorageRuntime& storage, unsigned boot = 1)
      : storage(storage), bootId(Id(90000 + boot)), effects(io), safetyPort(effects), safety(safetyPort),
      sequence(safety, true), control("claimed-device", bootId, sequence,
      storage.ControlJournal(), 1), state(false, 0, ""),
      runtime(control, sequence, state, "catalog"), adapter(effects, runtime), presentation(adapter) {
    calibration.fill({1100, 1900, 120});
    assert(effects.Configure(calibration, 1));
    effects.Tick(0);
    effects.Tick(now);
    assert(effects.RecoverAfterInspection());
    assert(runtime.BindPresentation(presentation));
    adapter.BindClock(Clock, this);
    adapter.BindBootIdentity(bootId);
    Recover();
  }
  static uint64_t Clock(void* context) { return static_cast<Rig*>(context)->now; }
  void Recover() {
    assert(effects.RecoverAfterInspection());
    for (unsigned attempt = 0; attempt != 8; ++attempt) {
      if (runtime.RecoverAfterInspection()) return;
      storage.RunOne();
    }
    assert(false);
  }
  void Tick() {
    now += 10;
    presentation.CheckDeadline(now);
    effects.Tick(now);
    SelectionStep step;
    runtime.Tick(now, step);
    presentation.CheckDeadline(now);
    adapter.Tick(now);
  }
  void Store(unsigned operation, bool shortVariant = false, uint64_t ttl = 30000) {
    assert(presentation.State() == WandPresentationState::Idle);
    assert(adapter.SelectVariant(shortVariant));
    assert(state.Read(snapshot));
    proposal = TurnProposal();
    proposal.proposalId = Id(operation);
    proposal.bootId = bootId;
    proposal.catalogVersion = snapshot.catalogVersion;
    proposal.generation = snapshot.generation;
    proposal.deviceRevision = snapshot.deviceRevision;
    proposal.operationEpoch = snapshot.operationEpoch;
    proposal.compartment = Slot;
    assert(pending.Store(proposal, snapshot, state, now, ttl));
    originalFingerprint = PhysicalPresentation::OperationFingerprint(shortVariant);
    originalShort = shortVariant;
    originalStoredAt = now;
    originalDeadline = now + ttl;
    startingServoWrites = io.servoWrites;
    startingVibrationWrites = io.vibrationWrites;
  }
  void ReserveQueued() {
    assert(!presentation.ReservePendingOperation(pending, state, now));
    assert(presentation.State() == WandPresentationState::AwaitingAdmission);
    assert(presentation.AdmissionIsPending());
    assert(presentation.ClaimedOperationFingerprint() == originalFingerprint);
    assert(presentation.ClaimedCalibrationRevision() == 1);
    assert(!state.Matches(snapshot));
    ConversationRuntimeSnapshot unavailable;
    assert(!state.Read(unavailable));
    assert(!adapter.SelectVariant(!originalShort));
    assert(!runtime.ReserveAutonomousPresentation(proposal.proposalId, Slot, snapshot, originalFingerprint));
    AssertNoMotion();
  }
  void ReserveAck() {
    assert(storage.RunOne());
    storage.ControlJournal().PollStorage();
    assert(storage.ControlJournal().HasPresentationReservation());
    assert(storage.ControlJournal().HasPendingStorage());
    Tick();
    assert(presentation.ReservePendingOperation(pending, state, now));
    assert(!storage.ControlJournal().HasPendingStorage());
    assert(!presentation.AdmissionIsPending());
    assert(runtime.HasStorageWork()); // The claimed intent still owns admission.
    AssertNoMotion();
    outcome = KeepsakeOutcome();
    outcome.operationId = Id(10000 + operationOrdinal());
    outcome.outcomeId = Id(20000 + operationOrdinal());
    outcome.proposalId = outcome.physicalOperationId = proposal.proposalId;
    outcome.bootId = proposal.bootId;
    outcome.generation = proposal.generation;
    outcome.deviceRevision = proposal.deviceRevision;
    outcome.operationEpoch = proposal.operationEpoch;
    outcome.compartment = Slot;
    outcome.physicalFingerprint = originalFingerprint;
  }
  unsigned operationOrdinal() const {
    return static_cast<unsigned>(std::stoul(proposal.proposalId.substr(24), nullptr, 16));
  }
  void Prepare() {
    const auto token = Submit(storage, Evidence::Operation::OnlinePrepare, outcome);
    Tick();
    AssertNoMotion();
    assert(!Contains(Source(storage).prepared, outcome));
    assert(storage.RunOne());
    assert(Consume(storage, token).status == Evidence::Status::Succeeded);
    assert(Contains(Source(storage).prepared, outcome));
    ConversationRuntimeSnapshot unavailable;
    assert(!state.Read(unavailable));
  }
  void BeginQueued() {
    assert(!presentation.StartPolled(pending, state, now));
    assert(presentation.AdmissionIsPending());
    assert(control.Revision() == snapshot.deviceRevision);
    AssertNoMotion();
  }
  void BeginAck() {
    assert(storage.RunOne());
    storage.ControlJournal().PollStorage();
    assert(storage.ControlJournal().HasPending());
    Tick(); // Producer polling owns the ACK; Tick must not start the cycle.
    AssertNoMotion();
    assert(control.Revision() == snapshot.deviceRevision);
    assert(presentation.StartPolled(pending, state, now));
    assert(presentation.State() == WandPresentationState::Raising);
    assert(control.Revision() == snapshot.deviceRevision + 1);
    assert(adapter.Diagnostics().calibrationId == 1);
    assert(std::strcmp(adapter.Diagnostics().variant, originalShort ? "short" : "main") == 0);
    assert(std::strcmp(adapter.Diagnostics().operationId.data(), proposal.proposalId.c_str()) == 0);
  }
  void AssertNoMotion() const {
    assert(io.servoWrites == startingServoWrites && io.vibrationWrites == startingVibrationWrites);
  }
  void UntilCloseQueued() {
    while (presentation.State() != WandPresentationState::Completing) {
      assert(now < originalStoredAt + 15000 && now < originalDeadline);
      Tick();
      assert(presentation.State() != WandPresentationState::Fault);
    }
    assert(control.HasAutonomousPresentation());
    assert(storage.ControlJournal().HasPendingStorage());
    assert(!runtime.HasValidatedAutonomousCompletion());
    assert(!io.run && io.servoWrites == startingServoWrites + 2);
    assert(Contains(Source(storage).prepared, outcome));
  }
  void UntilProofQueued() {
    UntilCloseQueued();
    assert(storage.RunOne());
    Tick();
    assert(presentation.State() == WandPresentationState::Completing);
    assert(runtime.HasValidatedAutonomousCompletion());
    assert(storage.ControlJournal().HasPendingStorage());
    assert(!HasKeepsakePhysicalEvidence(storage.ControlJournal(), outcome));
  }
  void Complete() {
    UntilProofQueued();
    assert(storage.RunOne());
    storage.ControlJournal().PollStorage();
    assert(HasKeepsakePhysicalEvidence(storage.ControlJournal(), outcome));
    assert(presentation.State() == WandPresentationState::Completing);
    assert(Contains(Source(storage).prepared, outcome));
    Tick();
    assert(presentation.State() == WandPresentationState::Idle);
    assert(!runtime.HasValidatedAutonomousCompletion());
    assert(std::strcmp(adapter.Diagnostics().state, "completed") == 0);
    assert(RunEvidence(storage, Evidence::Operation::OnlineComplete, outcome).status == Evidence::Status::Succeeded);
    assert(Contains(Source(storage).pending, outcome));
  }
  void Stop() {
    SelectionStep step;
    runtime.Stop(step);
    assert(presentation.State() == WandPresentationState::Fault);
    assert(!io.run && !runtime.HasValidatedAutonomousCompletion());
  }
  DeviceStorageRuntime& storage;
  std::string bootId;
  FakeIo io;
  PhysicalEffects effects;
  SafetyAdapter safetyPort;
  SafetyController safety;
  SelectionSequence sequence;
  DeviceControl control;
  ConversationRuntimeState state;
  DeviceControlRuntime runtime;
  PhysicalPresentation adapter;
  WandPresentation presentation;
  std::array<ServoCalibration, 6> calibration{};
  PendingProposal pending;
  TurnProposal proposal;
  ConversationRuntimeSnapshot snapshot;
  KeepsakeOutcome outcome;
  uint64_t now = 30, originalFingerprint = 0, originalStoredAt = 0, originalDeadline = 0;
  bool originalShort = false;
  int startingServoWrites = 0, startingVibrationWrites = 0;
};

void ExtendedRealPhysicalOwnerCycles() {
  constexpr unsigned Boots = 144, CyclesPerBoot = 8;
  static_assert(Boots * CyclesPerBoot == 1152, "Keep the measured workload explicit");
  DurableFixture durable;
  KeepsakeOutcome retained;
  uint64_t compactions = 0, lastEpoch = 0, elapsedMs = 0;
  for (unsigned boot = 0; boot != Boots; ++boot) {
    auto startup = durable.Start();
    auto* storage = startup->Runtime();
    Rig owner(*storage, boot + 1);
    for (unsigned cycle = 0; cycle != CyclesPerBoot; ++cycle) {
      const unsigned operation = boot * CyclesPerBoot + cycle + 1;
      owner.Store(operation, operation % 2 == 0);
      owner.ReserveQueued();
      owner.ReserveAck();
      owner.Prepare();
      owner.BeginQueued();
      owner.BeginAck();
      owner.Complete();
      if (operation == 1) retained = owner.outcome;
      else {
        assert(RunEvidence(*storage, Evidence::Operation::OnlineAck, owner.outcome).status == Evidence::Status::Succeeded);
        assert(!Contains(Source(*storage).pending, owner.outcome));
      }
      storage->RunOne(); // Only quiescent actual reconciliation may compact.
      owner.Tick();
      assert(Contains(Source(*storage).pending, retained));
      assert(HasKeepsakePhysicalEvidence(storage->ControlJournal(), retained));
      assert(owner.control.ReadyMask() == 63);
      lastEpoch = owner.control.OperationEpoch();
    }
    compactions += storage->Compactions();
    elapsedMs += owner.now - 30;
  }
  assert(compactions >= 2 && lastEpoch > retained.operationEpoch);
  auto startup = durable.Start();
  auto* storage = startup->Runtime();
  Rig reopened(*storage, 99);
  assert(Contains(Source(*storage).pending, retained));
  assert(HasKeepsakePhysicalEvidence(storage->ControlJournal(), retained));
  const auto before = reopened.io.servoWrites;
  assert(!reopened.control.ReserveAutonomousPresentation(retained.physicalOperationId, retained.compartment,
      reopened.control.Revision(), retained.physicalFingerprint, retained.operationEpoch));
  assert(!reopened.control.BeginAutonomousPresentation(retained.physicalOperationId, retained.compartment,
      reopened.control.Revision(), retained.physicalFingerprint, retained.operationEpoch));
  assert(reopened.io.servoWrites == before && !reopened.runtime.HasAutonomousPresentation());
  std::printf("autonomous production-startup workload: cycles=%u boots=%u verificationBoots=1 compactions=%llu finalEpoch=%llu simulatedMs=%llu delivered=%u retained=1\n",
      Boots * CyclesPerBoot, Boots, static_cast<unsigned long long>(compactions),
      static_cast<unsigned long long>(lastEpoch), static_cast<unsigned long long>(elapsedMs),
      Boots * CyclesPerBoot - 1);
}

void AnotherClaimCannotBorrowTheOwnedTransaction() {
  DurableFixture durable;
  auto storage = durable.Open();
  assert(storage->Boot());
  Rig owner(*storage);
  owner.Store(400);
  PendingProposal secondPending;
  PendingProposal::ClaimedProposal otherClaim;
  assert(secondPending.Store(owner.proposal, owner.snapshot, owner.state, owner.now, 30000));
  assert(secondPending.Claim(otherClaim, owner.state, owner.now));
  assert(!owner.runtime.ValidateClaimedAutonomousPresentation(otherClaim,
      owner.originalFingerprint, 2, owner.now));
  owner.ReserveQueued();
  using Progress = DeviceControlRuntime::PresentationProgress;
  assert(!owner.runtime.ValidateClaimedAutonomousPresentation(otherClaim,
      owner.originalFingerprint, 1, owner.now));
  assert(owner.runtime.ReserveClaimedAutonomousPresentation(otherClaim,
      owner.originalFingerprint, 1, owner.now) == Progress::Rejected);
  assert(owner.runtime.BeginClaimedAutonomousPresentation(otherClaim,
      owner.originalFingerprint, 1, owner.now) == Progress::Rejected);
  assert(owner.presentation.AdmissionIsPending());
  assert(storage->ControlJournal().HasPendingStorage());
  owner.AssertNoMotion();
  owner.ReserveAck(); // The original claim still consumes its own result.
  owner.Prepare();
  owner.BeginQueued();
  owner.BeginAck();
  owner.Complete();
}

enum class StopPhase { Reserve, Prepare, Begin, Close, Proof };
void StopAtEveryOwnedBoundary() {
  for (auto phase : {StopPhase::Reserve, StopPhase::Prepare, StopPhase::Begin, StopPhase::Close, StopPhase::Proof}) {
    for (bool writtenBeforeStop : {false, true}) {
      DurableFixture durable;
      auto storage = durable.Open();
      assert(storage->Boot());
      Rig owner(*storage);
      owner.Store(100);
      owner.ReserveQueued();
      Evidence::Token prepare;
      if (phase != StopPhase::Reserve) {
        owner.ReserveAck();
        if (phase == StopPhase::Prepare) prepare = Submit(*storage, Evidence::Operation::OnlinePrepare, owner.outcome);
        else {
          owner.Prepare();
          owner.BeginQueued();
          if (phase == StopPhase::Close || phase == StopPhase::Proof) {
            owner.BeginAck();
            if (phase == StopPhase::Proof) owner.UntilProofQueued();
            else owner.UntilCloseQueued();
          }
        }
      }
      if (writtenBeforeStop) assert(storage->RunOne());
      owner.Stop();
      const auto motionAtStop = owner.io.servoWrites;
      if (!writtenBeforeStop) assert(storage->RunOne());
      storage->ControlJournal().PollStorage();
      if (phase == StopPhase::Prepare) {
        const auto result = Consume(*storage, prepare);
        assert(result.status == (writtenBeforeStop ? Evidence::Status::Succeeded : Evidence::Status::CancelledBeforeWrite));
      }
      assert(!storage->ControlJournal().HasPendingStorage());
      owner.Tick();
      assert(owner.presentation.State() == WandPresentationState::Fault);
      assert(owner.io.servoWrites == motionAtStop && !owner.io.run);
      assert(!owner.runtime.HasValidatedAutonomousCompletion());
      assert(!Contains(Source(*storage).pending, owner.outcome));
      if (phase == StopPhase::Proof) assert(HasKeepsakePhysicalEvidence(storage->ControlJournal(), owner.outcome));
      else if (phase != StopPhase::Reserve) assert(!HasKeepsakePhysicalEvidence(storage->ControlJournal(), owner.outcome));
      owner.Recover();
      assert(!owner.runtime.HasValidatedAutonomousCompletion());
      assert(!owner.presentation.StartPolled(owner.pending, owner.state, owner.now));
      assert(owner.io.servoWrites == motionAtStop);
    }
  }
}

void LostAdmissionJournalAckNeverMoves() {
  for (bool begin : {false, true}) {
    for (bool afterWrite : {false, true}) {
      DurableFixture durable;
      auto storage = durable.Open();
      assert(storage->Boot());
      {
        Rig owner(*storage);
        owner.Store(200);
        owner.ReserveQueued();
        if (begin) {
          owner.ReserveAck();
          owner.Prepare();
          owner.BeginQueued();
        }
        durable.journal.cutBefore = !afterWrite;
        durable.journal.cutAfter = afterWrite;
        assert(storage->RunOne());
        owner.Tick();
        if (begin) assert(!owner.presentation.StartPolled(owner.pending, owner.state, owner.now));
        owner.AssertNoMotion();
        assert(!owner.runtime.HasAutonomousPresentation());
        assert(!owner.runtime.HasValidatedAutonomousCompletion());
        assert(!Contains(Source(*storage).pending, owner.outcome));
        storage->ControlJournal().PollStorage();
        assert(!storage->ControlJournal().HasPendingStorage());
      }
      storage.reset();
      durable.journal.cutBefore = durable.journal.cutAfter = false;
      auto reopened = durable.Open();
      assert(reopened->Boot() == !(begin && afterWrite));
      Rig recovered(*reopened, 2);
      assert(!recovered.runtime.HasAutonomousPresentation());
      assert(!recovered.runtime.HasValidatedAutonomousCompletion());
      assert(Source(*reopened).pending.empty());
      assert(!reopened->ControlJournal().WasTimedPresentationCompletedHistorical(1, Id(200), Slot,
          PhysicalPresentation::OperationFingerprint(false)));
    }
  }
}

void ClaimDeadlineAndCalibrationAreNotRenewed() {
  for (bool changedCalibration : {false, true}) {
    DurableFixture durable;
    auto storage = durable.Open();
    assert(storage->Boot());
    Rig owner(*storage);
    owner.Store(300, false, 60);
    owner.ReserveQueued();
    if (changedCalibration) assert(owner.effects.Configure(owner.calibration, 2));
    else for (unsigned tick = 0; tick != 6; ++tick) owner.Tick();
    if (changedCalibration) owner.Tick();
    assert(storage->RunOne());
    storage->ControlJournal().PollStorage();
    owner.Tick();
    owner.AssertNoMotion();
    assert(!owner.presentation.StartPolled(owner.pending, owner.state, owner.now));
    assert(!owner.runtime.HasAutonomousPresentation());
    assert(!owner.runtime.HasValidatedAutonomousCompletion());
  }
}
} // namespace

int main() {
  ExtendedRealPhysicalOwnerCycles();
  AnotherClaimCannotBorrowTheOwnedTransaction();
  StopAtEveryOwnedBoundary();
  LostAdmissionJournalAckNeverMoves();
  ClaimDeadlineAndCalibrationAreNotRenewed();
}

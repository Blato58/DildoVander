#include <algorithm>
#include <cassert>
#include <cstdio>
#include <cstring>
#include <memory>
#include <vector>

#include "../src/features/device_control/DeviceStorageRuntime.h"
#include "../src/features/device_control/DeviceStorageStartup.h"
#include "physical_effects_test_support.h"
#include "../src/features/voice_turn/VoiceTestContext.h"
#include "../src/features/voice_turn/LocalVoicePort.h"

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
    proposal.technicalTest = true;
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
  void CompleteTechnical() {
    bool closing = false, proving = false;
    for (unsigned attempt = 0; presentation.State() != WandPresentationState::Idle && attempt != 1500; ++attempt) {
      closing |= presentation.State() == WandPresentationState::Completing;
      proving |= runtime.HasValidatedAutonomousCompletion();
      storage.RunOne(); Tick();
      assert(presentation.State() != WandPresentationState::Fault);
    }
    assert(closing && proving && presentation.State() == WandPresentationState::Idle);
    assert(io.servoWrites == startingServoWrites + 2 && !io.run);
    assert(storage.ControlJournal().WasTimedPresentationCompletedHistorical(
        outcome.operationEpoch, outcome.physicalOperationId, Slot, outcome.physicalFingerprint));
    assert(storage.ControlJournal().IsTechnicalTestOperation(
        outcome.operationEpoch, outcome.physicalOperationId, Slot, outcome.physicalFingerprint));
    assert(!HasKeepsakePhysicalEvidence(storage.ControlJournal(), outcome));
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


void NoTicket(const DurableFixture& durable) {
  const std::string ticket(43, 'A');
  for (const auto* bytes : {&durable.journal.bytes, &durable.marker.bytes, &durable.online.bytes, &durable.offline.bytes})
    assert(std::search(bytes->begin(), bytes->end(), ticket.begin(), ticket.end()) == bytes->end());
}
void RefuseProductionReference(DeviceStorageRuntime& storage, const KeepsakeOutcome& outcome, DurableFixture& durable) {
  const auto onlineBytes = durable.online.bytes, offlineBytes = durable.offline.bytes;
  const auto onlineWrites = durable.online.writes, offlineWrites = durable.offline.writes;
  assert(RunEvidence(storage, Evidence::Operation::OnlinePrepare, outcome).status == Evidence::Status::Refused);
  kufr::offline::Outcome offline;
  std::copy(outcome.physicalOperationId.begin(), outcome.physicalOperationId.end(), offline.operationId.begin());
  std::copy(outcome.bootId.begin(), outcome.bootId.end(), offline.bootId.begin());
  const auto evening = Id(70000);
  std::copy(evening.begin(), evening.end(), offline.localEveningId.begin());
  offline.operationEpoch = outcome.operationEpoch; offline.physicalFingerprint = outcome.physicalFingerprint;
  offline.generation = std::max<uint64_t>(1, outcome.generation); offline.deviceRevision = outcome.deviceRevision;
  offline.slot = outcome.compartment; offline.startedAtMs = 10;
  Evidence::Request request; Evidence::Token token;
  assert(Evidence::MakeOfflineRequest(Evidence::Operation::OfflineReserve, offline, request));
  assert(storage.EvidenceRequests().TrySubmit(request, token));
  assert(storage.RunOne());
  assert(Consume(storage, token).status == Evidence::Status::Refused);
  assert(durable.online.writes == onlineWrites && durable.offline.writes == offlineWrites);
  assert(durable.online.bytes == onlineBytes && durable.offline.bytes == offlineBytes);
  const auto online = Source(storage);
  assert(online.prepared.empty() && online.pending.empty() && online.quarantined.empty());
  NoTicket(durable);
}
void TechnicalClaimBeforeMotionAndThroughTimedReturn() {
  DurableFixture durable;
  auto storage = durable.Open();
  assert(storage->Boot());
  Rig rig(*storage);
  rig.Store(1, true);
  rig.ReserveQueued();
  assert(durable.journal.bytes[4] == 4); // no marker is claimed before the actual storage commit
  rig.ReserveAck();
  assert(durable.journal.bytes[4] == 5);
  assert(storage->ControlJournal().IsTechnicalTestOperation(rig.outcome.operationEpoch,
      rig.proposal.proposalId, Slot, rig.originalFingerprint));
  RefuseProductionReference(*storage, rig.outcome, durable); // exact valid one-use reservation, before motion
  rig.BeginQueued(); rig.BeginAck();
  rig.CompleteTechnical();
  RefuseProductionReference(*storage, rig.outcome, durable);
  PersistentCommandJournal reboot(durable.journal);
  assert(reboot.IsHealthy() && !reboot.HasPending() && !reboot.HasPresentationReservation());
  assert(reboot.IsTechnicalTestOperation(rig.outcome.operationEpoch, rig.proposal.proposalId, Slot, rig.originalFingerprint));
  assert(!HasKeepsakePhysicalEvidence(reboot, rig.outcome));
  assert(!reboot.ReservePresentation(rig.outcome.operationEpoch, rig.proposal.proposalId, Slot, rig.originalFingerprint));
  NoTicket(durable);
}
void StopAndUncertainReserveNeverRelabelOrMove() {
  for (unsigned boundary = 0; boundary != 4; ++boundary) {
    DurableFixture durable;
    auto storage = durable.Open();
  assert(storage->Boot());
    KeepsakeOutcome original;
    {
      Rig rig(*storage);
      rig.Store(10 + boundary);
      rig.ReserveQueued();
      if (boundary > 0) {
        if (boundary == 1) assert(storage->RunOne()); // commit without adopting reserve ACK
        if (boundary > 1) {
          rig.ReserveAck();
          if (boundary == 3) { rig.BeginQueued(); assert(storage->RunOne()); }
          original = rig.outcome;
        }
      }
      SelectionStep step; rig.runtime.Stop(step);
      rig.AssertNoMotion();
      for (unsigned attempt = 0; attempt != 6; ++attempt) { storage->RunOne(); storage->ControlJournal().PollStorage(); }
      PersistentCommandJournal persisted(durable.journal);
      assert(!persisted.HasPresentationReservation());
      assert(persisted.IsTechnicalTestOperation(rig.snapshot.operationEpoch, rig.proposal.proposalId,
          Slot, rig.originalFingerprint) == (boundary > 0));
      assert(!persisted.WasTimedPresentationCompletedHistorical(rig.snapshot.operationEpoch, rig.proposal.proposalId,
          Slot, rig.originalFingerprint));
      original.physicalOperationId = rig.proposal.proposalId;
      original.operationEpoch = rig.snapshot.operationEpoch;
      original.physicalFingerprint = rig.originalFingerprint; original.compartment = Slot;
      assert(!HasKeepsakePhysicalEvidence(persisted, original));
    }
    storage.reset();
    storage = durable.Open();
    const bool admitted = storage->Boot();
    if (boundary == 3) {
      assert(!admitted && storage->CurrentState() == DeviceStorageRuntime::State::InspectionRequired);
      assert(storage->AllowsRecovery() && !storage->AllowsAdmission());
    } else assert(admitted);
    {
      Rig recovered(*storage, 2); // explicit inspection, never an automatic physical resume
      assert(recovered.io.servoWrites == 0);
      assert(storage->ControlJournal().IsTechnicalTestOperation(original.operationEpoch,
          original.physicalOperationId, Slot, original.physicalFingerprint) == (boundary > 0));
      ConversationRuntimeSnapshot now;
      assert(recovered.state.Read(now));
      if (boundary > 0) assert(!recovered.runtime.ReserveAutonomousPresentation(
          original.physicalOperationId, Slot, now, original.physicalFingerprint));
      assert(recovered.io.servoWrites == 0);
    }
    NoTicket(durable);
  }
}
void CarrierIsBoundedRamOnly() {
  VoiceTestContextLease lease;
  lease.value.assign(43, 'A');
  assert(ValidVoiceTestContext(lease.value));
  assert(!ValidVoiceTestContext(lease.value + "A"));
  assert(!ValidVoiceTestContext(std::string(42, 'A') + "="));
  assert(ValidVoiceTestContextId(std::string(64, 'a')));
  assert(!ValidVoiceTestContextId(std::string(64, 'A')));
  ClearVoiceTestContext(lease.value);
  assert(lease.value.empty());
}
} // namespace
int main() {
  CarrierIsBoundedRamOnly();
  TechnicalClaimBeforeMotionAndThroughTimedReturn();
  StopAndUncertainReserveNeverRelabelOrMove();
  std::puts("PASS trusted technical origin: async reserve/begin/Stop/restart/timed return and zero production evidence writes");
}

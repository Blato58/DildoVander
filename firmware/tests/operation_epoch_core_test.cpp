#include <cassert>
#include <algorithm>
#include <array>
#include <string>

#include "../src/features/device_control/OperationEpoch.h"
#include "../src/features/device_control/PersistentCommandJournal.h"
#include "../src/features/conversation/TurnOperationCodec.h"
#include "physical_effects_test_support.h"

namespace {

constexpr char OperationId[] = "523ef590-ce4d-4901-91b3-79de1bfe7e35";
constexpr char OtherId[] = "7a2fef87-0a80-4115-86f3-3a93ad984b82";

class Storage final : public JournalStorage {
 public:
  Storage() {
    PersistentCommandJournal journal(*this);
    assert(journal.PersistReadyMask(63));
    assert(journal.EstablishScopedIdentity("device", true));
  }
  JournalReadResult Read(uint8_t* output, size_t capacity, size_t& length) override {
    if (!present) return JournalReadResult::Missing;
    if (size > capacity) return JournalReadResult::Failed;
    std::copy(bytes.begin(), bytes.begin() + size, output);
    length = size;
    return JournalReadResult::Found;
  }
  bool Write(const uint8_t* data, size_t length) override {
    assert(length <= bytes.size());
    std::copy(data, data + length, bytes.begin());
    size = length; present = true; ++writes;
    return true;
  }
  std::array<uint8_t, PersistentCommandJournal::RecordSize> bytes{};
  size_t size = 0;
  unsigned writes = 0;
  bool present = false;
};

struct ScopedFixture {
  Storage storage;
  PersistentCommandJournal journal{storage};
  FakeIo io;
  PhysicalEffects effects{io};
  SafetyAdapter port{effects};
  SafetyController safety{port};
  SelectionSequence sequence{safety, true};
  DeviceControl control{"device", "boot", sequence, journal, 1};
  ConversationRuntimeState state{false, 0, ""};
  DeviceControlRuntime runtime{control, sequence, state, "catalog"};
  PhysicalPresentation adapter{effects, runtime};
  WandPresentation presentation{adapter};

  explicit ScopedFixture(const Storage& saved = Storage()) : storage(saved) {
    std::array<ServoCalibration, 6> calibration;
    calibration.fill({1100, 1900, 500});
    assert(effects.Configure(calibration, 1));
    effects.Tick(0); effects.Tick(30);
    assert(effects.RecoverAfterInspection());
    assert(runtime.BindPresentation(presentation));
    assert(runtime.RecoverAfterInspection());
  }
  ConversationRuntimeSnapshot Snapshot() const {
    ConversationRuntimeSnapshot snapshot;
    assert(state.Read(snapshot));
    return snapshot;
  }
  TurnProposal Proposal(const ConversationRuntimeSnapshot& snapshot) const {
    TurnProposal proposal;
    proposal.proposalId = OperationId;
    proposal.compartment = 3;
    proposal.catalogVersion = snapshot.catalogVersion;
    proposal.generation = snapshot.generation;
    proposal.deviceRevision = snapshot.deviceRevision;
    proposal.operationEpoch = snapshot.operationEpoch;
    return proposal;
  }
  void Tick(uint64_t now) {
    presentation.CheckDeadline(now);
    effects.Tick(now);
    SelectionStep step;
    runtime.Tick(now, step);
    presentation.CheckDeadline(now);
    adapter.Tick(now);
  }
};

SelectionCommand Manual(const ScopedFixture& fixture, uint64_t epoch) {
  SelectionCommand command;
  command.commandId = OperationId; command.deviceId = "device";
  command.bootId = "boot"; command.controlLeaseId = "lease";
  command.sequence = 1; command.expectedRevision = fixture.control.Revision();
  command.issuedAtDeviceMs = 30; command.maxAgeMs = 2000;
  command.compartment = 3; command.operationEpoch = epoch;
  return command;
}

void ScopedReservationPinsBeforeAdmissionAndCannotResumeAfterRestart() {
  ScopedFixture fixture;
  PendingProposal pending;
  const auto snapshot = fixture.Snapshot();
  const auto proposal = fixture.Proposal(snapshot);
  const auto fingerprint = fixture.presentation.CurrentOperationFingerprint();
  assert(pending.Store(proposal, snapshot, fixture.state, 30, 2000));
  assert(fixture.presentation.ReservePendingOperation(pending, fixture.state, 30));
  assert(fixture.journal.HasPresentationReservation() && !fixture.journal.HasPending());
  assert(fixture.journal.HistoryCount() == 1 && fixture.journal.IsEvidencePinned(
      1, OperationId, 3, fingerprint));
  assert(!fixture.control.WasTimedPresentationCompleted(OperationId, 3, 1, fingerprint));
  assert(fixture.io.servoWrites == 0 && fixture.io.vibrationWrites == 0);
  assert(!fixture.runtime.AcquireManualControl("competing", 31));

  ScopedFixture restarted(fixture.storage);
  assert(!restarted.journal.HasPresentationReservation() && !restarted.journal.HasPending());
  const auto current = restarted.Snapshot();
  PendingProposal repeated;
  assert(repeated.Store(restarted.Proposal(current), current, restarted.state, 30, 2000));
  assert(!restarted.presentation.ReservePendingOperation(repeated, restarted.state, 30));
  assert(!restarted.presentation.Start(repeated, restarted.state, 30));
  assert(restarted.io.servoWrites == 0 && restarted.io.vibrationWrites == 0);

  assert(fixture.presentation.Start(pending, fixture.state, 31));
  assert(!fixture.journal.HasPresentationReservation() && fixture.journal.HasPending());
  assert(fixture.journal.HistoryCount() == 1); // Consume never appends a duplicate history record.
  SelectionStep step;
  fixture.runtime.Stop(step);
  assert(!fixture.journal.HasPresentationReservation());

  ScopedFixture cancelled;
  const auto before = cancelled.Snapshot();
  PendingProposal cancelPending;
  assert(cancelPending.Store(cancelled.Proposal(before), before, cancelled.state, 30, 2000));
  assert(cancelled.presentation.ReservePendingOperation(cancelPending, cancelled.state, 30));
  cancelled.runtime.Stop(step);
  assert(!cancelled.journal.HasPresentationReservation() && !cancelled.journal.HasPending());
  assert(!cancelled.presentation.Start(cancelPending, cancelled.state, 31));
  assert(cancelled.io.servoWrites == 0);
}

void WrongEpochDoesNotConsumeOrMove() {
  ScopedFixture fixture;
  assert(fixture.runtime.AcquireManualControl("lease", 30));
  assert(fixture.presentation.CurrentOperationFingerprint() == 0);
  const uint64_t revision = fixture.control.Revision();
  const unsigned writes = fixture.storage.writes;
  for (uint64_t epoch : {uint64_t{0}, uint64_t{2}, uint64_t{UINT64_MAX}}) {
    const auto command = Manual(fixture, epoch);
    assert(fixture.runtime.AcceptSelection(command, true, 40).error ==
           CommandError::WrongOperationEpoch);
    InventoryReloadCommand reload;
    reload.deviceId = "device"; reload.bootId = "boot";
    reload.operationEpoch = epoch;
    assert(fixture.runtime.ConfirmReload(reload, true, 40).error ==
           CommandError::WrongOperationEpoch);
    SelectionStep step;
    assert(!fixture.runtime.BeginAcceptedSelection(40, step));
    assert(fixture.control.Revision() == revision);
    assert(fixture.storage.writes == writes && fixture.journal.IsHealthy());
    assert(!fixture.control.NeedsRecovery());
    assert(fixture.io.servoWrites == 0 && fixture.io.vibrationWrites == 0);
  }
  auto command = Manual(fixture, 1);
  assert(fixture.runtime.AcceptSelection(command, true, 40).error == CommandError::None);
  assert(fixture.runtime.AcceptSelection(command, true, 41).duplicate);
  assert(fixture.journal.HasPending());
  SelectionStep step;
  assert(fixture.runtime.BeginAcceptedSelection(40, step));
  uint64_t now = 50;
  for (; now <= 15000 && fixture.control.HasPendingSelection(); now += 10) {
    if (now % 1000 == 0) assert(fixture.runtime.RenewManualLease("lease", now));
    fixture.Tick(now);
  }
  assert(!fixture.control.HasPendingSelection() && !fixture.journal.HasPending());
  assert(fixture.control.LastResult().outcome == CommandOutcome::Completed);
  assert(fixture.journal.IsHealthy() && fixture.io.servoWrites == 2);
  // Keep this control owner and its RAM cache alive across an explicit rotation.
  assert(fixture.journal.AdvanceEpochAndCompact());
  assert(fixture.runtime.PublishCurrent());
  assert(fixture.runtime.AcceptSelection(command, true, now).error == CommandError::WrongOperationEpoch);
  auto next = command;
  next.operationEpoch = 2;
  next.sequence = 2;
  next.expectedRevision = fixture.control.Revision();
  next.issuedAtDeviceMs = now;
  assert(fixture.runtime.AcceptSelection(next, true, now).error == CommandError::None);
  fixture.runtime.Stop(step);
  const auto stoppedRetry = fixture.runtime.AcceptSelection(next, true, now + 1);
  assert(stoppedRetry.duplicate && stoppedRetry.outcome == CommandOutcome::OutcomeUnknown);

  ScopedFixture lease;
  assert(lease.runtime.AcquireManualControl("lease", 30));
  const auto stale = Manual(lease, 0);
  assert(lease.runtime.AcceptSelection(stale, true, 6029).error == CommandError::WrongOperationEpoch);
  assert(!lease.control.OwnsLiveManualLease("lease", 6030));
}

void ReloadCacheUsesOriginalEpoch() {
  ScopedFixture fixture;
  assert(fixture.runtime.AcquireManualControl("lease", 30));
  InventoryReloadCommand reload;
  reload.commandId = OperationId;
  reload.deviceId = "device"; reload.bootId = "boot"; reload.controlLeaseId = "lease";
  reload.sequence = 1; reload.expectedRevision = fixture.control.Revision();
  reload.issuedAtDeviceMs = 30; reload.maxAgeMs = 2000; reload.compartment = 3;
  reload.inspectionConfirmed = true; reload.operationEpoch = 1;
  assert(fixture.runtime.ConfirmReload(reload, true, 40).error == CommandError::None);
  assert(fixture.journal.AdvanceEpochAndCompact());
  assert(fixture.runtime.PublishCurrent());
  assert(fixture.runtime.ConfirmReload(reload, true, 41).error == CommandError::WrongOperationEpoch);
  reload.operationEpoch = 2;
  reload.sequence = 2;
  assert(fixture.runtime.ConfirmReload(reload, true, 42).error == CommandError::None);
  assert(fixture.runtime.ConfirmReload(reload, true, 43).duplicate);
  assert(fixture.io.servoWrites == 0 && fixture.io.vibrationWrites == 0 && fixture.journal.IsHealthy());
}

void ScopedPresentationCompletesAndRejectsOriginalAfterRotation() {
  ScopedFixture fixture;
  const auto original = fixture.Snapshot();
  assert(original.operationEpoch == 1);
  const uint64_t fingerprint = fixture.presentation.CurrentOperationFingerprint();
  assert(fingerprint == PhysicalPresentation::OperationFingerprint());
  const unsigned writes = fixture.storage.writes;
  for (uint64_t epoch : {uint64_t{0}, uint64_t{2}, uint64_t{UINT64_MAX}}) {
    auto stale = original; stale.operationEpoch = epoch;
    assert(!fixture.adapter.Raise(3, OperationId, stale));
    assert(!fixture.control.BeginAutonomousPresentation(OperationId, 3,
        fixture.control.Revision(), PhysicalPresentation::OperationFingerprint(), epoch));
    assert(fixture.storage.writes == writes && fixture.journal.IsHealthy());
    assert(!fixture.control.NeedsRecovery());
    assert(fixture.io.servoWrites == 0 && fixture.io.vibrationWrites == 0);
  }
  PendingProposal pending;
  const auto proposal = fixture.Proposal(original);
  assert(pending.Store(proposal, original, fixture.state, 30, 30000));
  assert(fixture.presentation.Start(pending, fixture.state, 30));
  assert(fixture.presentation.CurrentOperationFingerprint() == 0);
  uint64_t now = 40;
  for (; now <= 15000 && fixture.presentation.State() != WandPresentationState::Idle; now += 10)
    fixture.Tick(now);
  assert(fixture.presentation.State() == WandPresentationState::Idle);
  assert(fixture.io.servoWrites == 2 && fixture.journal.IsHealthy());
  assert(fixture.control.WasTimedPresentationCompleted(OperationId, 3, 1, fingerprint));
  assert(!fixture.control.WasTimedPresentationCompleted(OperationId, 3, 1, fingerprint + 1));
  assert(!fixture.control.WasTimedPresentationCompleted(OperationId, 3, 1, 0));
  const uint64_t shortFingerprint = fixture.presentation.CurrentOperationFingerprint();
  assert(shortFingerprint == PhysicalPresentation::OperationFingerprint(true));
  assert(shortFingerprint != fingerprint);
  const auto shortSnapshot = fixture.Snapshot();
  auto shortProposal = fixture.Proposal(shortSnapshot);
  shortProposal.proposalId = OtherId;
  const uint64_t shortStartedAt = now;
  assert(pending.Store(shortProposal, shortSnapshot, fixture.state, shortStartedAt, 30000));
  assert(fixture.presentation.Start(pending, fixture.state, shortStartedAt));
  for (now = shortStartedAt + 10; now <= shortStartedAt + 15000 && fixture.presentation.State() != WandPresentationState::Idle; now += 10)
    fixture.Tick(now);
  assert(fixture.presentation.State() == WandPresentationState::Idle);
  assert(fixture.control.WasTimedPresentationCompleted(OtherId, 3, 1, shortFingerprint));
  assert(!fixture.control.WasTimedPresentationCompleted(OtherId, 3, 1, fingerprint));
  assert(fixture.journal.AdvanceEpochAndCompact());
  assert(fixture.runtime.PublishCurrent());
  assert(!fixture.state.Matches(original));
  assert(fixture.Snapshot().operationEpoch == 2);
  assert(!fixture.adapter.Raise(3, OperationId, original));
  // The same presentation owner must accept a fresh identity in the new epoch.
  const auto nextSnapshot = fixture.Snapshot();
  auto nextProposal = fixture.Proposal(nextSnapshot);
  nextProposal.proposalId = OtherId;
  assert(!pending.Store(shortProposal, shortSnapshot, fixture.state, now, 30000));
  assert(pending.Store(nextProposal, nextSnapshot, fixture.state, now, 30000));
  assert(fixture.presentation.Start(pending, fixture.state, now));
  const uint64_t nextStartedAt = now;
  for (now += 10; now <= nextStartedAt + 15000 && fixture.presentation.State() != WandPresentationState::Idle; now += 10)
    fixture.Tick(now);
  assert(fixture.presentation.State() == WandPresentationState::Idle);
  assert(fixture.control.WasTimedPresentationCompleted(OtherId, 3, 2, shortFingerprint));
  ScopedFixture rebooted(fixture.storage);
  assert(rebooted.control.OperationEpoch() == 2);
  assert(!rebooted.adapter.Raise(3, OperationId, original));
  assert(!rebooted.control.BeginAutonomousPresentation(OtherId, 3,
      rebooted.control.Revision(), fingerprint, 1));
  assert(rebooted.io.servoWrites == 0 && rebooted.io.vibrationWrites == 0);
  assert(rebooted.journal.IsHealthy() && !rebooted.control.NeedsRecovery());
  assert(rebooted.control.WasTimedPresentationCompleted(OperationId, 3, 1, fingerprint));
  assert(rebooted.runtime.AcquireManualControl("lease", 30));
  assert(rebooted.runtime.AcceptSelection(Manual(rebooted, 1), true, 40).error ==
         CommandError::WrongOperationEpoch);
  assert(rebooted.io.servoWrites == 0 && rebooted.io.vibrationWrites == 0);
}

void PendingProposalRetainsAndInvalidatesCapturedEpoch() {
  ScopedFixture fixture;
  const auto snapshot = fixture.Snapshot();
  auto proposal = fixture.Proposal(snapshot);
  PendingProposal pending;
  proposal.operationEpoch = 0;
  assert(!pending.Store(proposal, snapshot, fixture.state, 30, 30000));
  proposal.operationEpoch = snapshot.operationEpoch;
  assert(pending.Store(proposal, snapshot, fixture.state, 30, 30000));
  assert(fixture.journal.AdvanceEpochAndCompact());
  assert(fixture.runtime.PublishCurrent());
  TurnProposal result;
  ConversationRuntimeSnapshot consumed;
  assert(!pending.Read(result, fixture.state, 40));
  assert(!pending.Consume(result, consumed, fixture.state, 40));
  assert(!fixture.state.Publish(snapshot.generation, snapshot.deviceRevision,
                                snapshot.readyMask, snapshot.catalogVersion, 0));
}

std::string TurnJson(const std::string& correlationEpoch, const std::string& proposalEpoch) {
  return std::string("{\"operationId\":\"") + OperationId +
      "\",\"kind\":\"turn\",\"status\":\"completed\",\"correlation\":{"
      "\"deviceId\":\"device\",\"bootId\":\"" + OtherId + "\","
      "\"conversationId\":\"" + OtherId + "\",\"clientTurnId\":\"" + OperationId + "\","
      "\"generation\":0,\"deviceRevision\":0" + correlationEpoch + "},"
      "\"result\":{\"reply\":\"ready\",\"decision\":\"propose\",\"reason\":\"\",\"proposal\":{"
      "\"proposalId\":\"" + OperationId + "\",\"conversationId\":\"" + OtherId + "\","
      "\"turnId\":\"" + OperationId + "\",\"catalogVersion\":\"catalog\","
      "\"deviceRevision\":0,\"compartment\":3,\"reason\":\"fits\",\"bootId\":\"" + OtherId + "\","
      "\"generation\":0,\"expiresAt\":\"2030-01-01T00:00:30Z\"" + proposalEpoch + "}},\"error\":null}";
}

void DecimalCodecAndPollGuards() {
  uint64_t epoch = 7;
  assert(ParseOperationEpoch("0", epoch) && epoch == 0);
  assert(ParseOperationEpoch("18446744073709551615", epoch) && epoch == UINT64_MAX);
  for (const char* invalid : {"", "00", "01", "+1", "-1", " 1", "1 ", "1.0", "18446744073709551616"})
    assert(!ParseOperationEpoch(invalid, epoch));
  const auto parse = [](const std::string& json, TurnOperationObservation& result) {
    return ParseTurnOperation(json.data(), json.size(), result);
  };
  TurnOperationObservation observed;
  assert(parse(TurnJson("", ""), observed));
  assert(observed.correlation.operationEpoch == 0 && observed.proposal.operationEpoch == 0);
  const std::string maximum = ",\"operationEpoch\":\"18446744073709551615\"";
  assert(parse(TurnJson(maximum, maximum), observed));
  assert(observed.correlation.operationEpoch == UINT64_MAX && observed.proposal.operationEpoch == UINT64_MAX);
  const auto expected = observed.correlation;
  assert(EvaluateTurnOperationWithProposal(OperationId, expected, 0, 0, "catalog", 63,
      1893456000, observed, UINT64_MAX) == TurnPollDecision::ProposalReady);
  assert(EvaluateTurnOperationWithProposal(OperationId, expected, 0, 0, "catalog", 63,
      1893456000, observed, 0) == TurnPollDecision::Discard);
  observed.proposal.operationEpoch = 0;
  assert(EvaluateTurnOperationWithProposal(OperationId, expected, 0, 0, "catalog", 63,
      1893456000, observed, UINT64_MAX) == TurnPollDecision::Discard);
  observed.correlation.operationEpoch = 0;
  assert(EvaluateTurnOperation(OperationId, expected, 0, 0, observed, UINT64_MAX) == TurnPollDecision::Discard);
  for (const char* invalid : {"0", "null", "\"01\"", "\"+1\"", "\"18446744073709551616\""}) {
    const std::string field = std::string(",\"operationEpoch\":") + invalid;
    assert(!parse(TurnJson(field, maximum), observed));
    assert(!parse(TurnJson(maximum, field), observed));
  }
  assert(!parse(TurnJson(maximum + maximum, maximum), observed));
  assert(!parse(TurnJson(maximum, maximum + maximum), observed));
}

}  // namespace

int main() {
  ScopedReservationPinsBeforeAdmissionAndCannotResumeAfterRestart();
  WrongEpochDoesNotConsumeOrMove();
  ReloadCacheUsesOriginalEpoch();
  ScopedPresentationCompletesAndRejectsOriginalAfterRotation();
  PendingProposalRetainsAndInvalidatesCapturedEpoch();
  DecimalCodecAndPollGuards();
}

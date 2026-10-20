#include <algorithm>
#include <cassert>
#include <cstdio>
#include <cstring>
#include <functional>
#include <iostream>
#include <vector>

#include "../src/features/device_control/EvidenceStorageOwner.h"
#include "../src/features/device_control/JournalAdmissionGate.h"
#include "../src/features/device_control/JournalStorageOwner.h"
#include "../src/features/device_control/PersistentCommandJournal.h"
#include "../src/features/keepsake/KeepsakeCommittedInventory.h"
#include "../src/features/offline_show/OfflineCommittedInventory.h"

namespace {
using Owner = EvidenceStorageOwner;
using Operation = Owner::Operation;
using Status = Owner::Status;
using Token = Owner::Token;
class Storage final : public JournalStorage {
 public:
  JournalReadResult Read(uint8_t* output, size_t capacity, size_t& length) override {
    length = 0;
    if (failRead) return JournalReadResult::Failed;
    if (bytes.empty()) return JournalReadResult::Missing;
    if (bytes.size() > capacity) return JournalReadResult::Failed;
    std::copy(bytes.begin(), bytes.end(), output); length = bytes.size();
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
  std::function<void()> duringWrite;
  unsigned writes = 0;
  bool cutBefore = false, cutAfter = false, failRead = false;
};
class Gate final : public JournalAdmissionGate {
 public:
  bool AllowsAdmission() const override { return admission; }
  bool AllowsRecovery() const override { return recovery; }
  bool ReconcileBeforeResume(uint32_t) override { return recovery; }
  bool admission = false, recovery = false;
};
std::string Id(unsigned ordinal) {
  char value[37];
  std::snprintf(value, sizeof(value), "%08x-1234-4234-8234-123456789abc", ordinal);
  return value;
}
KeepsakeOutcome Online(unsigned ordinal = 1) {
  KeepsakeOutcome outcome;
  outcome.operationId = Id(ordinal); outcome.outcomeId = Id(ordinal + 100);
  outcome.proposalId = Id(ordinal + 200); outcome.physicalOperationId = Id(ordinal + 300);
  outcome.bootId = Id(999); outcome.generation = 1; outcome.deviceRevision = 4;
  outcome.operationEpoch = 1; outcome.physicalFingerprint = ordinal + 700; outcome.compartment = 2;
  return outcome;
}
kufr::offline::Outcome Offline(unsigned ordinal = 1) {
  kufr::offline::Outcome outcome;
  const auto operation = Id(ordinal + 300), boot = Id(999), evening = Id(998);
  std::copy(operation.begin(), operation.end(), outcome.operationId.begin());
  std::copy(boot.begin(), boot.end(), outcome.bootId.begin());
  std::copy(evening.begin(), evening.end(), outcome.localEveningId.begin());
  outcome.generation = 1; outcome.startedAtMs = 10; outcome.slot = 2;
  outcome.operationEpoch = 1; outcome.physicalFingerprint = ordinal + 700; outcome.deviceRevision = 4;
  return outcome;
}
Owner::Request OnlineRequest(Operation operation, const KeepsakeOutcome& outcome, const char* reason = "") {
  Owner::Request request;
  assert(Owner::MakeOnlineRequest(operation, outcome, request, reason));
  return request;
}
Owner::Request OfflineRequest(Operation operation, const kufr::offline::Outcome& outcome) {
  Owner::Request request;
  assert(Owner::MakeOfflineRequest(operation, outcome, request));
  return request;
}
struct Fixture {
  Storage journalStorage, onlineStorage, offlineStorage;
  PersistentCommandJournal journal{journalStorage};
  KeepsakeCommittedInventory online{onlineStorage};
  kufr::offline::OfflineCommittedInventory offline{offlineStorage};
  JournalTransactionArbiter arbiter;
  JournalStorageOwner journalOwner{journal, arbiter};
  Owner owner{journal, online, offline, arbiter};
  Fixture() {
    assert(journal.EstablishScopedIdentity("evidence-device", true));
    assert(journal.PersistReadyMask(63));
    assert(online.Establish("evidence-device", true));
    assert(offline.EstablishBinding("evidence-device", true));
    assert(owner.OpenAndPublish());
  }
  void Reserve(const KeepsakeOutcome& outcome) {
    assert(journal.ReservePresentation(outcome.operationEpoch, outcome.physicalOperationId,
        outcome.compartment, outcome.physicalFingerprint));
  }
  void CompletePhysical(const KeepsakeOutcome& outcome) {
    assert(journal.BeginReservedPresentation(outcome.operationEpoch, outcome.physicalOperationId,
        outcome.compartment, outcome.physicalFingerprint));
    assert(journal.ResolveRestored(outcome.compartment));
    assert(journal.ConfirmTimedPresentationScoped(outcome.operationEpoch, outcome.physicalOperationId,
        outcome.compartment, outcome.physicalFingerprint));
  }
  Owner::Completion Run(const Owner::Request& request, bool release = true) {
    Token token;
    assert(owner.TrySubmit(request, token));
    assert(!arbiter.TryEnter());
    assert(owner.RunOne());
    Owner::Completion result;
    assert(owner.TryReadCompletion(token, result));
    if (release) assert(owner.ReleaseCompletion(token));
    return result;
  }
};

void ExplicitBindingAndRequestBounds() {
  Storage journalStorage, onlineStorage, offlineStorage;
  PersistentCommandJournal journal(journalStorage);
  KeepsakeCommittedInventory online(onlineStorage);
  kufr::offline::OfflineCommittedInventory offline(offlineStorage);
  JournalTransactionArbiter arbiter;
  const auto writes = journalStorage.writes + onlineStorage.writes + offlineStorage.writes;
  Owner owner(journal, online, offline, arbiter);
  assert(!owner.IsHealthy());
  assert(!owner.OpenAndPublish());
  assert(writes == journalStorage.writes + onlineStorage.writes + offlineStorage.writes);
  KeepsakeOutboxState onlineState; kufr::offline::Outbox offlineState;
  assert(!owner.TryReadOnline(onlineState) && !owner.TryReadOffline(offlineState));
  Owner::Request request;
  auto outcome = Online(); outcome.bootId.append(10000, 'x');
  assert(!Owner::MakeOnlineRequest(Operation::OnlinePrepare, outcome, request));
  const std::string reason(65, 'x');
  assert(!Owner::MakeOnlineRequest(Operation::OnlineRetire, Online(), request, reason.c_str()));
  assert(!Owner::MakeOnlineRequest(Operation::OnlineRetire, Online(), request, "unknown_reason"));
  request = OnlineRequest(Operation::OnlinePrepare, Online()); request.online.outcomeId[36] = 'x';
  Token token;
  assert(!owner.TrySubmit(request, token) && !token.id);
}

void CapacityAndSameArbiter() {
  Fixture fixture;
  const auto outcome = Online(); fixture.Reserve(outcome);
  Token token, extra;
  assert(fixture.owner.TrySubmit(OnlineRequest(Operation::OnlinePrepare, outcome), token));
  assert(!fixture.owner.TrySubmit(OnlineRequest(Operation::OnlinePrepare, outcome), extra));
  assert(fixture.owner.RunOne());
  Owner::Completion result;
  assert(fixture.owner.TryReadCompletion(token, result) && result.status == Status::Succeeded);
  assert(!fixture.arbiter.TryEnter());
  JournalStorageOwner::Request journalRequest;
  journalRequest.operation = JournalStorageOwner::Operation::Resolve; journalRequest.readyMask = 63;
  JournalStorageOwner::Token journalToken;
  assert(fixture.journalOwner.TrySubmit(journalRequest, JournalStorageOwner::Purpose::Recovery, journalToken));
  assert(fixture.journalOwner.RunOne());
  JournalStorageOwner::Completion journalResult;
  assert(fixture.journalOwner.TryReadCompletion(journalToken, journalResult));
  assert(fixture.journalOwner.ReleaseCompletion(journalToken));
  assert(!fixture.arbiter.TryEnter()); // Evidence result still owns the shared barrier.
  assert(!fixture.owner.ReleaseCompletion(extra));
  assert(!fixture.owner.TrySubmit(OnlineRequest(Operation::OnlinePrepare, outcome), extra));
  assert(fixture.owner.ReleaseCompletion(token));
  assert(fixture.arbiter.TryEnter()); fixture.arbiter.Leave();
  for (unsigned ordinal = 2; ordinal <= 8; ++ordinal) {
    fixture.journal.CancelPresentationReservation();
    const auto next = Online(ordinal); fixture.Reserve(next);
    assert(fixture.Run(OnlineRequest(Operation::OnlinePrepare, next)).status == Status::Succeeded);
  }
  fixture.journal.CancelPresentationReservation();
  const auto overflow = Online(9); fixture.Reserve(overflow);
  assert(fixture.Run(OnlineRequest(Operation::OnlinePrepare, overflow)).status == Status::Refused);
  KeepsakeOutboxState state;
  assert(fixture.owner.TryReadOnline(state) && state.prepared.size() == 8);
}

void GateIsCheckedAtSubmitAndExecution() {
  Fixture fixture;
  Gate gate;
  Owner owner(fixture.journal, fixture.online, fixture.offline, fixture.arbiter, &gate);
  assert(owner.OpenAndPublish());
  const auto outcome = Online(); fixture.Reserve(outcome);
  Token token;
  const auto request = OnlineRequest(Operation::OnlinePrepare, outcome);
  assert(!owner.TrySubmit(request, token));
  gate.admission = true;
  assert(owner.TrySubmit(request, token));
  gate.admission = false;
  const auto writes = fixture.onlineStorage.writes;
  assert(owner.RunOne());
  Owner::Completion completion;
  assert(owner.TryReadCompletion(token, completion) && completion.status == Status::Refused);
  assert(fixture.onlineStorage.writes == writes && owner.ReleaseCompletion(token));
  Owner::Request enabled; enabled.operation = Operation::OfflineEnabled;
  enabled.setEnabled = true; enabled.enabled = true;
  assert(!owner.TrySubmit(enabled, token));
  gate.recovery = true;
  assert(owner.TrySubmit(enabled, token));
  gate.recovery = false;
  assert(owner.RunOne());
  assert(owner.TryReadCompletion(token, completion) && completion.status == Status::Refused);
  assert(owner.ReleaseCompletion(token));
}

void ExactCurrentReferenceGuard() {
  Fixture fixture;
  auto outcome = Online();
  assert(fixture.Run(OnlineRequest(Operation::OnlinePrepare, outcome)).status == Status::Refused);
  fixture.Reserve(outcome);
  auto changed = outcome; changed.operationEpoch = 2;
  assert(fixture.Run(OnlineRequest(Operation::OnlinePrepare, changed)).status == Status::Refused);
  changed = outcome; ++changed.physicalFingerprint;
  assert(fixture.Run(OnlineRequest(Operation::OnlinePrepare, changed)).status == Status::Refused);
  fixture.journal.CancelPresentationReservation();
  // A durable Same tombstone alone is not current admission authority.
  assert(fixture.Run(OnlineRequest(Operation::OnlinePrepare, outcome)).status == Status::Refused);
  assert(fixture.journal.ReleaseEvidencePin(outcome.operationEpoch, outcome.physicalOperationId,
      outcome.compartment, outcome.physicalFingerprint));
  assert(fixture.Run(OnlineRequest(Operation::OnlinePrepare, outcome)).status == Status::Refused);
  KeepsakeOutboxState state;
  assert(fixture.owner.TryReadOnline(state) && state.prepared.empty());
}

void CancelBeforeAndDuringWrite() {
  {
    Fixture fixture; const auto outcome = Online(); fixture.Reserve(outcome);
    Token token;
    assert(fixture.owner.TrySubmit(OnlineRequest(Operation::OnlinePrepare, outcome), token));
    const auto writes = fixture.onlineStorage.writes;
    fixture.arbiter.CancelAdmissions();
    assert(fixture.owner.RunOne());
    Owner::Completion result;
    assert(fixture.owner.TryReadCompletion(token, result));
    assert(result.status == Status::CancelledBeforeWrite && !result.current && fixture.onlineStorage.writes == writes);
    assert(!fixture.arbiter.TryEnter()); assert(fixture.owner.ReleaseCompletion(token));
  }
  {
    Fixture fixture; const auto outcome = Online(); fixture.Reserve(outcome);
    fixture.onlineStorage.duringWrite = [&]() {
      fixture.arbiter.CancelAdmissions();
      assert(!fixture.arbiter.TryEnter());
      Owner::Completion result;
      assert(!fixture.owner.TryReadCompletion({}, result));
    };
    auto result = fixture.Run(OnlineRequest(Operation::OnlinePrepare, outcome), false);
    assert(result.status == Status::Succeeded && !result.current);
    KeepsakeOutboxState state;
    assert(fixture.owner.TryReadOnline(state) && state.prepared.size() == 1 && result.onlineGeneration == 2);
    assert(!fixture.arbiter.TryEnter());
    assert(fixture.owner.ReleaseCompletion(result.token));
    assert(fixture.arbiter.TryEnter()); fixture.arbiter.Leave();
  }
}

void SourceCutsRequireReopen() {
  for (bool after : {false, true}) {
    Fixture fixture; const auto outcome = Online(); fixture.Reserve(outcome);
    fixture.onlineStorage.cutBefore = !after; fixture.onlineStorage.cutAfter = after;
    assert(fixture.Run(OnlineRequest(Operation::OnlinePrepare, outcome)).status == Status::Unknown);
    KeepsakeOutboxState state;
    assert(!fixture.owner.TryReadOnline(state) && !fixture.owner.IsHealthy());
    fixture.onlineStorage.cutBefore = fixture.onlineStorage.cutAfter = false;
    // Removing the fault never turns an unknown ACK into success.
    assert(fixture.Run(OnlineRequest(Operation::OnlinePrepare, outcome)).status == Status::Unknown);
    assert(fixture.owner.OpenAndPublish());
    assert(fixture.owner.IsHealthy());
    assert(fixture.owner.TryReadOnline(state) && state.prepared.size() == (after ? 1u : 0u));
  }
}

void CompletionAndTerminalTuples() {
  Fixture fixture; const auto outcome = Online(); fixture.Reserve(outcome);
  assert(fixture.Run(OnlineRequest(Operation::OnlinePrepare, outcome)).status == Status::Succeeded);
  assert(fixture.Run(OnlineRequest(Operation::OnlineComplete, outcome)).status == Status::Refused);
  fixture.CompletePhysical(outcome);
  auto changed = outcome; ++changed.deviceRevision;
  assert(fixture.Run(OnlineRequest(Operation::OnlineComplete, changed)).status == Status::Refused);
  fixture.arbiter.CancelAdmissions();
  assert(fixture.Run(OnlineRequest(Operation::OnlineComplete, outcome)).status == Status::Succeeded);
  assert(fixture.Run(OnlineRequest(Operation::OnlineAck, changed)).status == Status::Refused);
  assert(fixture.Run(OnlineRequest(Operation::OnlineAck, outcome)).status == Status::Succeeded);
  KeepsakeOutboxState state;
  assert(fixture.owner.TryReadOnline(state) && state.pending.empty() && state.prepared.empty());
  assert(state.quarantined.size() == 1 && state.quarantined[0].reason == "outcome_delivered"
      && ExactKeepsakeDisposition(state.quarantined[0], outcome));
  assert(fixture.Run(OnlineRequest(Operation::OnlineAck, outcome)).status == Status::Succeeded);
  const auto writes = fixture.onlineStorage.writes;
  uint64_t generation = 0, afterGeneration = 0;
  assert(fixture.owner.TryReadOnline(state, &generation));
  // The terminal schema stores physical identity, not the complete envelope.
  // No-op acknowledgement of that exact tuple asserts no new metadata proof.
  assert(fixture.Run(OnlineRequest(Operation::OnlineAck, changed)).status == Status::Succeeded);
  assert(fixture.owner.TryReadOnline(state, &afterGeneration));
  assert(fixture.onlineStorage.writes == writes && generation == afterGeneration);
}

void RetirementPreservesTimedEvidence() {
  Fixture fixture; const auto outcome = Online(); fixture.Reserve(outcome);
  assert(fixture.Run(OnlineRequest(Operation::OnlinePrepare, outcome)).status == Status::Succeeded);
  fixture.CompletePhysical(outcome);
  fixture.arbiter.CancelAdmissions();
  assert(fixture.Run(OnlineRequest(Operation::OnlineRetire, outcome, "proposal_stopped")).status == Status::Succeeded);
  KeepsakeOutboxState state;
  assert(fixture.owner.TryReadOnline(state) && state.prepared.empty() && state.pending.size() == 1);
  assert(state.quarantined.empty());
}

void PermanentRetirementIsOneAtomicTransaction() {
  for (unsigned cut = 0; cut < 3; ++cut) {
    Fixture fixture; const auto outcome = Online(); fixture.Reserve(outcome);
    assert(fixture.Run(OnlineRequest(Operation::OnlinePrepare, outcome)).status == Status::Succeeded);
    fixture.CompletePhysical(outcome);
    assert(fixture.Run(OnlineRequest(Operation::OnlineComplete, outcome)).status == Status::Succeeded);
    const auto writes = fixture.onlineStorage.writes;
    fixture.onlineStorage.cutBefore = cut == 1; fixture.onlineStorage.cutAfter = cut == 2;
    const auto result = fixture.Run(OnlineRequest(Operation::OnlineRetire, outcome, "outcome_deleted"));
    assert(result.status == (cut ? Status::Unknown : Status::Succeeded));
    assert(fixture.onlineStorage.writes == writes + 1);
    fixture.onlineStorage.cutBefore = fixture.onlineStorage.cutAfter = false;
    assert(fixture.owner.OpenAndPublish());
    KeepsakeOutboxState state;
    assert(fixture.owner.TryReadOnline(state));
    if (cut == 1) {
      assert(state.pending.size() == 1 && SameKeepsakeCandidate(state.pending[0], outcome));
      assert(state.quarantined.empty());
    } else {
      assert(state.pending.empty() && state.prepared.empty() && state.quarantined.size() == 1);
      assert(state.quarantined[0].reason == "outcome_deleted"
          && ExactKeepsakeDisposition(state.quarantined[0], outcome));
    }
  }
}

void OfflineMergeAndExactAck() {
  Fixture fixture; const auto online = Online(); fixture.Reserve(online);
  const auto original = Offline();
  assert(fixture.Run(OfflineRequest(Operation::OfflineReserve, original)).status == Status::Succeeded);
  Owner::Request settings;
  settings.operation = Operation::OfflineSettings; settings.serverSettingsRevision = 10;
  settings.setQuiet = settings.setAmbient = true; settings.quiet = true; settings.ambient = false;
  assert(fixture.Run(settings).status == Status::Succeeded);
  auto completed = original; completed.completed = true; completed.completedAtMs = 200;
  assert(fixture.Run(OfflineRequest(Operation::OfflineComplete, completed)).status == Status::Refused);
  fixture.CompletePhysical(online);
  assert(fixture.Run(OfflineRequest(Operation::OfflineComplete, completed)).status == Status::Succeeded);
  settings.serverSettingsRevision = 9; settings.quiet = false; settings.ambient = true;
  assert(fixture.Run(settings).status == Status::Refused);
  auto wrong = completed; ++wrong.deviceRevision;
  assert(fixture.Run(OfflineRequest(Operation::OfflineAck, wrong)).status == Status::Refused);
  wrong = completed; ++wrong.completedAtMs;
  assert(fixture.Run(OfflineRequest(Operation::OfflineAck, wrong)).status == Status::Refused);
  fixture.arbiter.CancelAdmissions();
  assert(fixture.Run(OfflineRequest(Operation::OfflineAck, completed)).status == Status::Succeeded);
  kufr::offline::Outbox state;
  assert(fixture.owner.TryReadOffline(state) && state.quiet && !state.ambientEnabled && state.serverSettingsRevision == 10);
  assert(!state.records[0].operationId[0]);
}

void OfflineUnknownRowsAndWriteCuts() {
  Fixture fixture;
  kufr::offline::Outbox legacy;
  assert(fixture.offline.Read(legacy));
  legacy.records[6].slot = 3; // Unknown legacy bytes are neither a free slot nor an ACK target.
  assert(fixture.offline.Write(legacy));
  assert(fixture.owner.OpenAndPublish());
  Owner::Request enabled; enabled.operation = Operation::OfflineEnabled;
  enabled.setEnabled = true; enabled.enabled = true;
  assert(fixture.Run(enabled).status == Status::Succeeded);
  kufr::offline::Outbox state;
  assert(fixture.owner.TryReadOffline(state) && state.records[6].slot == 3 && state.offlineEnabled);
  fixture.offlineStorage.cutAfter = true;
  enabled.enabled = false;
  assert(fixture.Run(enabled).status == Status::Unknown && !fixture.owner.TryReadOffline(state));
  fixture.offlineStorage.cutAfter = false;
  assert(fixture.Run(enabled).status == Status::Unknown);
  assert(fixture.owner.OpenAndPublish());
  assert(fixture.owner.TryReadOffline(state) && !state.offlineEnabled && state.records[6].slot == 3);
}

void OfflineCapacityCutsAndCancelledReservation() {
  {
    Fixture fixture;
    for (unsigned ordinal = 1; ordinal <= 8; ++ordinal) {
      fixture.journal.CancelPresentationReservation();
      fixture.Reserve(Online(ordinal));
      assert(fixture.Run(OfflineRequest(Operation::OfflineReserve, Offline(ordinal))).status == Status::Succeeded);
    }
    fixture.journal.CancelPresentationReservation(); fixture.Reserve(Online(9));
    assert(fixture.Run(OfflineRequest(Operation::OfflineReserve, Offline(9))).status == Status::Refused);
    kufr::offline::Outbox state;
    assert(fixture.owner.TryReadOffline(state));
    for (const auto& record : state.records) assert(record.operationId[0]);
  }
  for (bool after : {false, true}) {
    Fixture fixture; fixture.Reserve(Online());
    fixture.offlineStorage.cutBefore = !after; fixture.offlineStorage.cutAfter = after;
    assert(fixture.Run(OfflineRequest(Operation::OfflineReserve, Offline())).status == Status::Unknown);
    kufr::offline::Outbox state;
    assert(!fixture.owner.TryReadOffline(state));
    fixture.offlineStorage.cutBefore = fixture.offlineStorage.cutAfter = false;
    assert(fixture.owner.OpenAndPublish());
    assert(fixture.owner.TryReadOffline(state) && bool(state.records[0].operationId[0]) == after);
  }
  {
    Fixture fixture; fixture.Reserve(Online());
    Token token;
    assert(fixture.owner.TrySubmit(OfflineRequest(Operation::OfflineReserve, Offline()), token));
    fixture.arbiter.CancelAdmissions();
    assert(fixture.owner.RunOne());
    Owner::Completion result;
    assert(fixture.owner.TryReadCompletion(token, result) && result.status == Status::CancelledBeforeWrite);
    assert(fixture.owner.ReleaseCompletion(token));
  }
}

void OnlineUnknownLegacyReferenceIsPreserved() {
  Fixture fixture;
  auto legacy = Online(50); legacy.operationEpoch = 0; legacy.physicalFingerprint = 0;
  assert(fixture.online.Commit({{}, {}, {legacy}}));
  assert(fixture.owner.OpenAndPublish());
  const auto outcome = Online(); fixture.Reserve(outcome);
  assert(fixture.Run(OnlineRequest(Operation::OnlinePrepare, outcome)).status == Status::Succeeded);
  KeepsakeOutboxState state;
  assert(fixture.owner.TryReadOnline(state) && state.prepared.size() == 2);
  assert(std::any_of(state.prepared.begin(), state.prepared.end(), [&](const KeepsakeOutcome& saved) {
    return SameKeepsakeCandidate(saved, legacy);
  }));
  const auto writes = fixture.onlineStorage.writes;
  assert(fixture.Run(OnlineRequest(Operation::OnlineRetire, legacy, "physical_outcome_unknown")).status == Status::Refused);
  assert(fixture.Run(OnlineRequest(Operation::OnlineAck, legacy)).status == Status::Refused);
  assert(fixture.onlineStorage.writes == writes);
  auto forged = legacy; forged.operationEpoch = 1; forged.physicalFingerprint = outcome.physicalFingerprint;
  assert(fixture.Run(OnlineRequest(Operation::OnlineComplete, forged)).status == Status::Refused);
  assert(fixture.owner.TryReadOnline(state) && state.prepared.size() == 2);
}
}

int main() {
  ExplicitBindingAndRequestBounds(); CapacityAndSameArbiter(); ExactCurrentReferenceGuard();
  GateIsCheckedAtSubmitAndExecution();
  CancelBeforeAndDuringWrite(); SourceCutsRequireReopen(); CompletionAndTerminalTuples();
  RetirementPreservesTimedEvidence(); OfflineMergeAndExactAck(); OfflineUnknownRowsAndWriteCuts();
  PermanentRetirementIsOneAtomicTransaction();
  OfflineCapacityCutsAndCancelledReservation(); OnlineUnknownLegacyReferenceIsPreserved();
  std::cout << "evidence storage owner tests passed\n";
}

#include <algorithm>
#include <cassert>
#include <cstdio>
#include <cstdlib>
#include <functional>
#include <iostream>
#include <limits>
#include <new>
#include <vector>

#include "../src/features/keepsake/KeepsakeEvidenceBridge.h"
#include "../src/features/keepsake/KeepsakeCommittedInventory.h"
#include "../src/features/device_control/PersistentCommandJournal.h"
#include "../src/features/offline_show/OfflineCommittedInventory.h"

namespace {
bool countAllocations = false;
size_t allocations = 0;
bool allowIo = false;
}
void* operator new(size_t size) {
  if (countAllocations) ++allocations;
  if (void* memory = std::malloc(size ? size : 1)) return memory;
  throw std::bad_alloc();
}
void* operator new[](size_t size) { return ::operator new(size); }
void operator delete(void* memory) noexcept { std::free(memory); }
void operator delete[](void* memory) noexcept { std::free(memory); }
void operator delete(void* memory, size_t) noexcept { std::free(memory); }
void operator delete[](void* memory, size_t) noexcept { std::free(memory); }

namespace {
using Bridge = KeepsakeEvidenceBridge;
using Owner = EvidenceStorageOwner;
using Operation = Owner::Operation;
using Transfer = Bridge::Transfer;
using Prepare = Bridge::PrepareResult;
using Status = Bridge::PrepareStatus;

struct IoScope {
  bool previous = allowIo;
  IoScope() { allowIo = true; }
  ~IoScope() { allowIo = previous; }
};
// Every control call in this scope must use fixed RAM, including first calls.
struct ControlScope {
  size_t previousAllocations = allocations;
  bool previousIo = allowIo;
  ControlScope() { allowIo = false; countAllocations = true; }
  ~ControlScope() {
    countAllocations = false; allowIo = previousIo;
    assert(allocations == previousAllocations);
  }
};
class Storage final : public JournalStorage {
 public:
  JournalReadResult Read(uint8_t* output, size_t capacity, size_t& length) override {
    assert(allowIo); ++reads; length = 0;
    if (bytes.empty()) return JournalReadResult::Missing;
    if (bytes.size() > capacity) return JournalReadResult::Failed;
    std::copy(bytes.begin(), bytes.end(), output); length = bytes.size();
    return JournalReadResult::Found;
  }
  bool Write(const uint8_t* input, size_t length) override {
    assert(allowIo); ++writes;
    if (duringWrite) duringWrite();
    if (cutBefore) return false;
    bytes.assign(input, input + length);
    return !cutAfter;
  }
  std::vector<uint8_t> bytes;
  std::function<void()> duringWrite;
  size_t reads = 0, writes = 0;
  bool cutBefore = false, cutAfter = false;
};
class Gate final : public JournalAdmissionGate {
 public:
  bool AllowsAdmission() const override { return admission; }
  bool AllowsRecovery() const override { return recovery; }
  bool ReconcileBeforeResume(uint32_t) override { return false; }
  bool admission = true, recovery = true;
};
std::string Id(unsigned ordinal) {
  char text[37];
  std::snprintf(text, sizeof(text), "00000000-0000-4000-8000-%012u", ordinal);
  return text;
}
KeepsakeOutcome Outcome(unsigned ordinal = 1) {
  KeepsakeOutcome outcome;
  outcome.operationId = Id(ordinal); outcome.outcomeId = Id(ordinal + 100);
  outcome.proposalId = Id(ordinal + 200); outcome.physicalOperationId = Id(ordinal + 300);
  outcome.bootId = Id(999); outcome.generation = 1; outcome.deviceRevision = 4;
  outcome.operationEpoch = 1; outcome.physicalFingerprint = ordinal + 700; outcome.compartment = 2;
  return outcome;
}
Bridge::Outcome Fixed(const KeepsakeOutcome& outcome) {
  Owner::Request request;
  assert(Owner::MakeOnlineRequest(Operation::OnlinePrepare, outcome, request));
  return request.online;
}
struct Fixture {
  IoScope constructionIo;
  Storage journalStorage, onlineStorage, offlineStorage;
  PersistentCommandJournal journal{journalStorage};
  KeepsakeCommittedInventory online{onlineStorage};
  kufr::offline::OfflineCommittedInventory offline{offlineStorage};
  JournalTransactionArbiter arbiter;
  Gate gate;
  Owner owner{journal, online, offline, arbiter, &gate};
  Bridge bridge{owner, gate};
  Fixture() {
    assert(journal.EstablishScopedIdentity("bridge-device", true));
    assert(journal.PersistReadyMask(63));
    assert(online.Establish("bridge-device", true));
    assert(offline.EstablishBinding("bridge-device", true));
    assert(owner.OpenAndPublish());
    allowIo = false;
  }
  void Reserve(const KeepsakeOutcome& outcome) {
    IoScope io;
    assert(journal.ReservePresentation(outcome.operationEpoch, outcome.physicalOperationId,
        outcome.compartment, outcome.physicalFingerprint));
  }
  void Physical(const KeepsakeOutcome& outcome) {
    IoScope io;
    assert(journal.BeginReservedPresentation(outcome.operationEpoch, outcome.physicalOperationId,
        outcome.compartment, outcome.physicalFingerprint));
    assert(journal.ResolveRestored(outcome.compartment));
    assert(journal.ConfirmTimedPresentationScoped(outcome.operationEpoch, outcome.physicalOperationId,
        outcome.compartment, outcome.physicalFingerprint));
  }
  void Run() { IoScope io; assert(owner.RunOne()); }
  Bridge::Handle Prepared(const KeepsakeOutcome& outcome) {
    assert(bridge.Bootstrap()); Reserve(outcome);
    const auto fixed = Fixed(outcome);
    Bridge::Handle handle;
    { ControlScope control; assert(bridge.TryPrepare(fixed, handle) == Prepare::Pending); }
    assert(bridge.Pump()); Run(); assert(bridge.Pump());
    assert(bridge.PollPrepare(handle) == Status::DurablePrepared);
    bridge.Pump(); // Release the adopted source token.
    assert(!owner.Busy());
    return handle;
  }
};

void AdmissionBoundsAndMailbox() {
  Fixture fixture;
  const auto outcome = Outcome(); const auto fixed = Fixed(outcome);
  Bridge::Handle handle;
  { ControlScope control; assert(fixture.bridge.TryPrepare(fixed, handle) == Prepare::Refused); }
  assert(fixture.bridge.Bootstrap());
  fixture.gate.admission = false;
  { ControlScope control; assert(fixture.bridge.TryPrepare(fixed, handle) == Prepare::Refused); }
  fixture.gate.admission = true; fixture.Reserve(outcome);
  Owner::Request offline;
  offline.operation = Operation::OfflineEnabled; offline.setEnabled = true; offline.enabled = true;
  Owner::Token offlineToken;
  assert(fixture.owner.TrySubmit(offline, offlineToken));
  { ControlScope control;
    assert(fixture.bridge.TryPrepare(fixed, handle) == Prepare::Pending);
    assert(fixture.bridge.TryPrepare(fixed, handle) == Prepare::Pending);
    auto changed = fixed; ++changed.deviceRevision;
    assert(fixture.bridge.TryPrepare(changed, handle) == Prepare::Refused);
    changed = fixed; ++changed.retryAtMs;
    assert(fixture.bridge.TryPrepare(changed, handle) == Prepare::Refused);
  }
  assert(!fixture.bridge.Pump()); // Offline owns the only owner mailbox.
  assert(fixture.bridge.PollPrepare(handle) == Status::Pending);
  fixture.Run(); Owner::Completion completion;
  assert(fixture.owner.TryReadCompletion(offlineToken, completion));
  assert(fixture.owner.ReleaseCompletion(offlineToken));
  assert(fixture.bridge.Pump()); fixture.Run(); assert(fixture.bridge.Pump());
  { ControlScope control; assert(fixture.bridge.PollPrepare(handle) == Status::DurablePrepared); }
  fixture.bridge.Pump();
}

void EightSlotsAndGenerations() {
  Fixture fixture; assert(fixture.bridge.Bootstrap());
  std::array<Bridge::Handle, Bridge::Capacity> handles{};
  for (size_t index = 0; index < handles.size(); ++index) {
    const auto fixed = Fixed(Outcome(static_cast<unsigned>(index + 1)));
    ControlScope control;
    assert(fixture.bridge.TryPrepare(fixed, handles[index]) == Prepare::Pending);
  }
  const auto ninth = Fixed(Outcome(90)); Bridge::Handle extra;
  { ControlScope control;
    assert(fixture.bridge.TryPrepare(ninth, extra) == Prepare::Busy && !extra.generation);
    assert(fixture.bridge.QueueComplete(handles[1]) == Transfer::RetainedVolatile);
    assert(fixture.bridge.QueueRetire(handles[0], "proposal_stopped") == Transfer::RetainedVolatile);
    assert(fixture.bridge.PollPrepare(handles[0]) == Status::CancelledBeforeWrite);
    assert(fixture.bridge.Release(handles[0]));
    assert(fixture.bridge.TryPrepare(ninth, extra) == Prepare::Pending);
    assert(extra.slot == handles[0].slot && extra.generation != handles[0].generation);
    assert(fixture.bridge.QueueComplete(handles[0]) == Transfer::Rejected);
    assert(fixture.bridge.QueueRetire(handles[2], "proposal_stopped") == Transfer::RetainedVolatile);
    assert(fixture.bridge.QueueRetire(handles[2], "proposal_cancelled") == Transfer::Rejected);
  }
  assert(fixture.bridge.Occupied() == Bridge::Capacity);
}

void StopDuringPrepareWrite() {
  Fixture fixture; const auto outcome = Outcome(); const auto fixed = Fixed(outcome);
  assert(fixture.bridge.Bootstrap()); fixture.Reserve(outcome);
  Bridge::Handle handle;
  assert(fixture.bridge.TryPrepare(fixed, handle) == Prepare::Pending);
  assert(fixture.bridge.Pump());
  fixture.onlineStorage.duringWrite = [&] { fixture.arbiter.CancelAdmissions(); };
  fixture.Run(); fixture.onlineStorage.duringWrite = {};
  assert(fixture.bridge.Pump());
  { ControlScope control;
    assert(fixture.bridge.PollPrepare(handle) == Status::Refused);
    assert(!fixture.bridge.Release(handle));
  }
  assert(!fixture.arbiter.TryEnter()); // Result remains leased after adoption.
  assert(fixture.bridge.Pump()); // Release preparation and submit retained retirement.
  fixture.Run(); assert(fixture.bridge.Pump()); fixture.bridge.Pump();
  assert(fixture.bridge.Release(handle));
  assert(fixture.bridge.Occupied() == 0 && !fixture.bridge.IsPoisoned());
}

void RetirementPromotesProofAndExactAck() {
  Fixture fixture; const auto outcome = Outcome();
  const auto handle = fixture.Prepared(outcome); fixture.Physical(outcome);
  { ControlScope control;
    assert(fixture.bridge.QueueRetire(handle, "proposal_stopped") == Transfer::RetainedVolatile);
    assert(fixture.bridge.QueueComplete(handle) == Transfer::RetainedVolatile);
  }
  assert(fixture.bridge.Pump()); fixture.Run(); assert(fixture.bridge.Pump());
  assert(!fixture.bridge.Release(handle));
  Bridge::Pending pending;
  assert(fixture.bridge.LookupPending(0, pending));
  auto wrong = pending; ++wrong.outcome.physicalFingerprint;
  assert(fixture.bridge.QueueValidatedAck(wrong) == Transfer::Rejected);
  wrong = pending; ++wrong.outcome.operationEpoch;
  assert(fixture.bridge.QueueValidatedAck(wrong) == Transfer::Rejected);
  assert(fixture.bridge.QueueValidatedAck(pending) == Transfer::RetainedVolatile);
  assert(fixture.bridge.QueueValidatedAck(pending) == Transfer::AlreadyRetained);
  assert(!fixture.bridge.LookupPending(0, pending));
  assert(fixture.bridge.Pump()); fixture.Run();
  assert(!fixture.arbiter.TryEnter());
  assert(fixture.bridge.Pump());
  assert(!fixture.arbiter.TryEnter()); // Ledger cannot be pruned before adoption/release.
  fixture.bridge.Pump();
  assert(fixture.arbiter.TryEnter()); fixture.arbiter.Leave();
  assert(fixture.bridge.Release(handle));
  assert(fixture.bridge.Occupied() == 0);
}

void CompleteInFlightSurvivesRetirement() {
  Fixture fixture; const auto outcome = Outcome();
  const auto handle = fixture.Prepared(outcome); fixture.Physical(outcome);
  assert(fixture.bridge.QueueComplete(handle) == Transfer::RetainedVolatile);
  assert(fixture.bridge.Pump());
  assert(fixture.bridge.QueueRetire(handle, "proposal_stopped") == Transfer::RetainedVolatile);
  fixture.Run(); assert(fixture.bridge.Pump());
  fixture.bridge.Pump();
  Bridge::Pending pending;
  assert(fixture.bridge.LookupPending(0, pending));
  assert(!fixture.bridge.Release(handle) && !fixture.bridge.IsPoisoned());
}

void PermanentFailureAfterProofWins(bool completionInFlight) {
  Fixture fixture; const auto outcome = Outcome();
  const auto handle = fixture.Prepared(outcome); fixture.Physical(outcome);
  if (completionInFlight) {
    assert(fixture.bridge.QueueComplete(handle) == Transfer::RetainedVolatile);
    assert(fixture.bridge.Pump());
    assert(fixture.bridge.QueueRetire(handle, "proposal_stopped") == Transfer::RetainedVolatile);
  } else {
    assert(fixture.bridge.QueueRetire(handle, "proposal_stopped") == Transfer::RetainedVolatile);
    assert(fixture.bridge.Pump());
  }
  fixture.Run(); assert(fixture.bridge.Pump()); fixture.bridge.Pump();
  Bridge::Pending pending;
  assert(fixture.bridge.LookupPending(0, pending));
  { ControlScope control;
    assert(fixture.bridge.QueueRetire(handle, "proposal_cancelled") == Transfer::AlreadyRetained);
  }
  assert(fixture.bridge.LookupPending(0, pending));
  assert(fixture.bridge.QueueRetire(handle, "outcome_deleted") == Transfer::RetainedVolatile);
  assert(!fixture.bridge.LookupPending(0, pending));
  assert(fixture.bridge.Pump()); fixture.Run(); assert(fixture.bridge.Pump());
  assert(fixture.bridge.QueueRetire(handle, "outcome_conflict") == Transfer::Rejected);
  fixture.bridge.Pump();
  KeepsakeOutboxState state;
  assert(fixture.owner.TryReadOnline(state));
  assert(state.pending.empty() && state.prepared.empty() && state.quarantined.size() == 1);
  assert(state.quarantined[0].reason == "outcome_deleted");
  assert(ExactKeepsakeDisposition(state.quarantined[0], outcome));
  assert(fixture.bridge.Release(handle));
}

void UnknownNeverReopens(bool cutAfter) {
  Fixture fixture; const auto outcome = Outcome(); const auto fixed = Fixed(outcome);
  assert(fixture.bridge.Bootstrap()); fixture.Reserve(outcome);
  Bridge::Handle handle;
  assert(fixture.bridge.TryPrepare(fixed, handle) == Prepare::Pending);
  assert(fixture.bridge.Pump());
  fixture.onlineStorage.cutBefore = !cutAfter; fixture.onlineStorage.cutAfter = cutAfter;
  fixture.Run(); assert(fixture.bridge.Pump()); fixture.bridge.Pump();
  assert(!fixture.owner.Busy());
  { ControlScope control;
    assert(fixture.bridge.PollPrepare(handle) == Status::Unknown);
    assert(!fixture.bridge.Release(handle));
    assert(fixture.bridge.QueueComplete(handle) == Transfer::Rejected);
  }
  fixture.onlineStorage.cutBefore = fixture.onlineStorage.cutAfter = false;
  { IoScope io; assert(fixture.owner.OpenAndPublish()); }
  assert(!fixture.bridge.Bootstrap());
  const auto next = Fixed(Outcome(2)); Bridge::Handle second;
  assert(fixture.bridge.TryPrepare(next, second) == Prepare::Refused);
  assert(fixture.bridge.IsPoisoned() && fixture.bridge.Occupied() == 1);
}

void BootstrapPendingPreparedAndLegacy() {
  Fixture fixture;
  KeepsakeOutboxState state;
  for (unsigned index = 1; index <= Bridge::Capacity; ++index) state.pending.push_back(Outcome(index));
  state.prepared.push_back(state.pending[0]); // One physical candidate owns one slot.
  state.pending[1].operationEpoch = state.pending[1].physicalFingerprint = 0;
  { IoScope io; assert(fixture.online.Commit(state)); assert(fixture.owner.OpenAndPublish()); }
  assert(fixture.bridge.Bootstrap());
  assert(fixture.bridge.Occupied() == Bridge::Capacity);
  Bridge::Handle handle;
  assert(fixture.bridge.TryPrepare(Fixed(Outcome(90)), handle) == Prepare::Busy);
  Bridge::Pending pending;
  assert(fixture.bridge.LookupPending(0, pending));
  assert(pending.outcome.operationEpoch && pending.outcome.physicalFingerprint);
  assert(!fixture.bridge.Release(pending.handle));
}

void LostAckPinsOldRuntimeAndBootReadsCommittedDisposition() {
  Fixture fixture; const auto outcome = Outcome();
  const auto handle = fixture.Prepared(outcome); fixture.Physical(outcome);
  assert(fixture.bridge.QueueComplete(handle) == Transfer::RetainedVolatile);
  assert(fixture.bridge.Pump()); fixture.Run(); assert(fixture.bridge.Pump());
  fixture.bridge.Pump();
  Bridge::Pending pending;
  assert(fixture.bridge.LookupPending(0, pending));
  assert(fixture.bridge.QueueValidatedAck(pending) == Transfer::RetainedVolatile);
  assert(fixture.bridge.Pump()); fixture.onlineStorage.cutAfter = true;
  fixture.Run(); assert(fixture.bridge.Pump()); fixture.bridge.Pump();
  assert(fixture.bridge.IsPoisoned() && !fixture.bridge.Release(handle));
  assert(!fixture.bridge.LookupPending(0, pending));
  fixture.onlineStorage.cutAfter = false;
  { IoScope io; assert(fixture.owner.OpenAndPublish()); }
  KeepsakeOutboxState committed;
  assert(fixture.owner.TryReadOnline(committed));
  assert(committed.pending.size() == 1); // ACK ledger persisted, removal did not.
  assert(committed.quarantined.size() == 1 && committed.quarantined[0].reason == "outcome_delivered");
  const auto refused = [&](Operation operation, const KeepsakeOutcome& candidate, const char* reason) {
    Owner::Request request; Owner::Token token; Owner::Completion result;
    assert(Owner::MakeOnlineRequest(operation, candidate, request, reason));
    assert(fixture.owner.TrySubmit(request, token)); fixture.Run();
    assert(fixture.owner.TryReadCompletion(token, result));
    assert(result.status == Owner::Status::Refused);
    assert(fixture.owner.ReleaseCompletion(token));
    assert(fixture.owner.TryReadOnline(committed) && committed.pending.size() == 1);
  };
  auto wrong = outcome; ++wrong.physicalFingerprint;
  refused(Operation::OnlineAck, wrong, "");
  refused(Operation::OnlineRetire, outcome, "outcome_conflict");
  // The old bridge does not reuse capacity or resume HTTP after an authoritative
  // reopen. Recovery/reconciliation of both source facts belongs to the runtime.
  assert(!fixture.bridge.Bootstrap() && fixture.bridge.Occupied() == 1);
  Bridge recovered(fixture.owner, fixture.gate);
  assert(recovered.Bootstrap());
  assert(!recovered.LookupPending(0, pending));
  assert(recovered.Pump()); fixture.Run(); assert(recovered.Pump()); recovered.Pump();
  assert(!recovered.LookupPending(0, pending) && !recovered.IsPoisoned());
  assert(fixture.owner.TryReadOnline(committed));
  assert(committed.pending.empty() && recovered.Occupied() == 0);
  assert(fixture.bridge.Occupied() == 1);
}

void LocalCancellationHandoffIsRetrySafe() {
  Fixture fixture; assert(fixture.bridge.Bootstrap());
  const auto fixed = Fixed(Outcome()); Bridge::Handle handle;
  { ControlScope control;
    assert(fixture.bridge.TryPrepare(fixed, handle) == Prepare::Pending);
    assert(!fixture.bridge.TransferToNetwork(handle));
    assert(fixture.bridge.QueueRetire(handle, "proposal_stopped") == Transfer::RetainedVolatile);
    assert(fixture.bridge.QueueRetire(handle, "proposal_stopped") == Transfer::AlreadyRetained);
    assert(fixture.bridge.QueueRetire(handle, "proposal_cancelled") == Transfer::Rejected);
    assert(fixture.bridge.TransferToNetwork(handle));
    assert(fixture.bridge.TransferToNetwork(handle));
    assert(fixture.bridge.Occupied() == 1);
  }
  fixture.bridge.Pump();
  assert(fixture.bridge.Occupied() == 0 && !fixture.owner.Busy());
}

void VolatileRetryAndTransferredTerminal() {
  Fixture fixture; const auto outcome = Outcome();
  const auto handle = fixture.Prepared(outcome); fixture.Physical(outcome);
  { ControlScope control;
    assert(fixture.bridge.QueueComplete(handle) == Transfer::RetainedVolatile);
    assert(fixture.bridge.TransferToNetwork(handle));
  }
  assert(fixture.bridge.Pump()); fixture.Run(); assert(fixture.bridge.Pump()); fixture.bridge.Pump();
  assert(fixture.bridge.Occupied() == 1); // A pending obligation never auto-releases.
  Bridge::Pending pending;
  assert(fixture.bridge.LookupPending(0, pending));
  const auto reads = fixture.onlineStorage.reads, writes = fixture.onlineStorage.writes;
  auto wrong = pending; ++wrong.outcome.physicalFingerprint;
  assert(fixture.bridge.QueueDeferred(wrong, 100) == Transfer::Rejected);
  assert(fixture.bridge.QueueDeferred(pending, 100) == Transfer::RetainedVolatile);
  assert(!fixture.bridge.LookupPending(2099, wrong));
  assert(fixture.bridge.LookupPending(2100, wrong));
  assert(wrong.outcome.retryDelayMs == 2000 && wrong.outcome.retryAtMs == 2100);
  assert(fixture.bridge.QueueDeferred(pending, 999999) == Transfer::AlreadyRetained);
  fixture.bridge.Pump(); // Publication refresh must not reset the RAM schedule.
  assert(!fixture.bridge.LookupPending(2099, pending));
  assert(fixture.bridge.LookupPending(2100, pending));
  assert(pending.outcome.retryAtMs == 2100 && pending.outcome.retryDelayMs == 2000);
  KeepsakeOutboxState state;
  assert(fixture.owner.TryReadOnline(state) && state.pending.size() == 1);
  assert(state.pending[0].retryAtMs == 0 && state.pending[0].retryDelayMs == 1000);
  assert(fixture.onlineStorage.reads == reads && fixture.onlineStorage.writes == writes);
  fixture.arbiter.CancelAdmissions();
  assert(fixture.bridge.QueueDeferred(pending, 2100) == Transfer::RetainedVolatile);
  assert(!fixture.bridge.LookupPending(6099, wrong));
  assert(fixture.bridge.LookupPending(6100, wrong));
  assert(fixture.bridge.QueueValidatedAck(pending) == Transfer::RetainedVolatile);
  assert(fixture.bridge.QueueDeferred(wrong, 9000) == Transfer::AlreadyRetained);
  assert(!fixture.bridge.LookupPending(std::numeric_limits<uint64_t>::max(), wrong));
  assert(fixture.bridge.Pump()); fixture.Run(); assert(fixture.bridge.Pump());
  assert(fixture.bridge.Occupied() == 1 && !fixture.arbiter.TryEnter());
  { ControlScope control;
    assert(fixture.bridge.QueueComplete(handle) == Transfer::AlreadyRetained);
    assert(fixture.bridge.TransferToNetwork(handle));
  }
  fixture.bridge.Pump();
  assert(fixture.bridge.Occupied() == 0 && !fixture.bridge.TransferToNetwork(handle));
}

void RetryCapSaturationAndRebootReset() {
  Fixture fixture; const auto outcome = Outcome();
  const auto handle = fixture.Prepared(outcome); fixture.Physical(outcome);
  assert(fixture.bridge.QueueComplete(handle) == Transfer::RetainedVolatile);
  assert(fixture.bridge.Pump()); fixture.Run(); assert(fixture.bridge.Pump()); fixture.bridge.Pump();
  Bridge::Pending pending;
  uint64_t now = 0;
  for (unsigned index = 0; index < 6; ++index) {
    assert(fixture.bridge.LookupPending(now, pending));
    assert(fixture.bridge.QueueDeferred(pending, now) == Transfer::RetainedVolatile);
    now += std::min<uint32_t>(pending.outcome.retryDelayMs * 2, 30000);
  }
  assert(fixture.bridge.LookupPending(now, pending) && pending.outcome.retryDelayMs == 30000);
  const auto maximum = std::numeric_limits<uint64_t>::max();
  assert(fixture.bridge.QueueDeferred(pending, maximum - 100) == Transfer::RetainedVolatile);
  assert(!fixture.bridge.LookupPending(maximum - 1, pending));
  assert(fixture.bridge.LookupPending(maximum, pending));
  assert(pending.outcome.retryAtMs == maximum && pending.outcome.retryDelayMs == 30000);
  Bridge rebooted(fixture.owner, fixture.gate); assert(rebooted.Bootstrap());
  assert(rebooted.LookupPending(0, pending));
  assert(pending.outcome.retryAtMs == 0 && pending.outcome.retryDelayMs == 1000);
  assert(fixture.bridge.QueueDeferred(pending, 0) == Transfer::AlreadyRetained);
}

void TransferredUnknownKeepsCapacity() {
  Fixture fixture; const auto outcome = Outcome(); const auto fixed = Fixed(outcome);
  assert(fixture.bridge.Bootstrap()); fixture.Reserve(outcome);
  Bridge::Handle handle;
  assert(fixture.bridge.TryPrepare(fixed, handle) == Prepare::Pending);
  assert(fixture.bridge.QueueComplete(handle) == Transfer::RetainedVolatile);
  assert(fixture.bridge.TransferToNetwork(handle));
  assert(fixture.bridge.Pump()); fixture.onlineStorage.cutAfter = true;
  fixture.Run(); assert(fixture.bridge.Pump()); fixture.bridge.Pump();
  assert(fixture.bridge.IsPoisoned() && fixture.bridge.Occupied() == 1);
  assert(!fixture.bridge.TransferToNetwork(handle) && !fixture.bridge.Release(handle));
}

void AutomaticRetirementTransfersWithoutChangingReason() {
  Fixture fixture; const auto outcome = Outcome(); const auto fixed = Fixed(outcome);
  assert(fixture.bridge.Bootstrap()); fixture.Reserve(outcome);
  Bridge::Handle handle;
  assert(fixture.bridge.TryPrepare(fixed, handle) == Prepare::Pending);
  assert(fixture.bridge.Pump());
  fixture.onlineStorage.duringWrite = [&] { fixture.arbiter.CancelAdmissions(); };
  fixture.Run(); fixture.onlineStorage.duringWrite = {};
  assert(fixture.bridge.Pump());
  { ControlScope control;
    assert(fixture.bridge.QueueRetire(handle, "proposal_stopped") == Transfer::Rejected);
    assert(fixture.bridge.TransferToNetwork(handle));
    assert(fixture.bridge.TransferToNetwork(handle));
  }
  assert(fixture.bridge.Pump()); fixture.Run(); assert(fixture.bridge.Pump());
  KeepsakeOutboxState state;
  assert(fixture.owner.TryReadOnline(state) && state.quarantined.size() == 1);
  assert(state.quarantined[0].reason == "proposal_cancelled" && state.prepared.empty());
  assert(fixture.bridge.Occupied() == 1);
  fixture.bridge.Pump(); assert(fixture.bridge.Occupied() == 0);
}
}

int main() {
  AdmissionBoundsAndMailbox();
  EightSlotsAndGenerations();
  StopDuringPrepareWrite();
  RetirementPromotesProofAndExactAck();
  CompleteInFlightSurvivesRetirement();
  PermanentFailureAfterProofWins(false);
  PermanentFailureAfterProofWins(true);
  UnknownNeverReopens(false); UnknownNeverReopens(true);
  BootstrapPendingPreparedAndLegacy();
  LostAckPinsOldRuntimeAndBootReadsCommittedDisposition();
  LocalCancellationHandoffIsRetrySafe();
  VolatileRetryAndTransferredTerminal();
  RetryCapSaturationAndRebootReset();
  TransferredUnknownKeepsCapacity();
  AutomaticRetirementTransfersWithoutChangingReason();
  std::cout << "Keepsake evidence bridge checks passed\n";
}

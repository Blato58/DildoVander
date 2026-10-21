#include <algorithm>
#include <cassert>
#include <cstdio>
#include <cstdlib>
#include <functional>
#include <iostream>
#include <new>
#include <vector>

#include "../src/features/offline_show/OfflineEvidenceBridge.h"
#include "../src/features/offline_show/OfflineCommittedInventory.h"
#include "../src/features/keepsake/KeepsakeCommittedInventory.h"
#include "../src/features/device_control/PersistentCommandJournal.h"

namespace {
bool allowIo = false, measureAllocations = false;
size_t allocations = 0;
}
void* operator new(size_t size) {
  if (measureAllocations) ++allocations;
  if (void* memory = std::malloc(size ? size : 1)) return memory;
  throw std::bad_alloc();
}
void* operator new[](size_t size) { return ::operator new(size); }
void operator delete(void* memory) noexcept { std::free(memory); }
void operator delete[](void* memory) noexcept { std::free(memory); }
void operator delete(void* memory, size_t) noexcept { std::free(memory); }
void operator delete[](void* memory, size_t) noexcept { std::free(memory); }

namespace {
using Bridge = kufr::offline::OfflineEvidenceBridge;
using Outcome = kufr::offline::Outcome;
using Outbox = kufr::offline::Outbox;
using Owner = EvidenceStorageOwner;
using Transfer = Bridge::Transfer;
using Status = Bridge::Status;
using ReserveStatus = Bridge::ReserveStatus;
using Lane = Bridge::Lane;
struct IoScope {
  bool previous = allowIo;
  IoScope() { allowIo = true; }
  ~IoScope() { allowIo = previous; }
};
struct ControlScope {
  bool previousIo = allowIo;
  size_t previousAllocations = allocations;
  ControlScope() { allowIo = false; measureAllocations = true; }
  ~ControlScope() {
    measureAllocations = false; allowIo = previousIo;
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
Outcome Original(unsigned ordinal = 1) {
  Outcome result;
  const auto operation = Id(ordinal), boot = Id(998), evening = Id(999);
  std::copy(operation.begin(), operation.end(), result.operationId.begin());
  std::copy(boot.begin(), boot.end(), result.bootId.begin());
  std::copy(evening.begin(), evening.end(), result.localEveningId.begin());
  result.generation = 1; result.startedAtMs = 20; result.slot = 2;
  result.operationEpoch = 1; result.physicalFingerprint = 700 + ordinal; result.deviceRevision = 4;
  result.scripted = true;
  return result;
}
Owner::Request Settings(uint64_t revision, bool quiet, bool ambient) {
  Owner::Request request;
  request.operation = Owner::Operation::OfflineSettings;
  request.setQuiet = request.setAmbient = true; request.quiet = quiet; request.ambient = ambient;
  request.serverSettingsRevision = revision;
  request.fenceStopRevision = true;
  return request;
}
Owner::Request Enabled(bool enabled) {
  Owner::Request request;
  request.operation = Owner::Operation::OfflineEnabled; request.setEnabled = true; request.enabled = enabled;
  request.fenceStopRevision = true;
  return request;
}
Owner::Request Stop(uint64_t revision) {
  auto request = Settings(0, true, false);
  request.setEnabled = true; request.enabled = false; request.serverStopRevision = revision;
  request.fenceStopRevision = false;
  return request;
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
    assert(journal.EstablishScopedIdentity("offline-bridge-device", true));
    assert(journal.PersistReadyMask(63));
    assert(online.Establish("offline-bridge-device", true));
    assert(offline.EstablishBinding("offline-bridge-device", true));
    assert(owner.OpenAndPublish());
    allowIo = false;
  }
  void ReserveJournal(const Outcome& outcome) {
    IoScope io;
    assert(journal.ReservePresentation(outcome.operationEpoch, outcome.operationId.data(), outcome.slot, outcome.physicalFingerprint));
  }
  void Physical(const Outcome& outcome) {
    IoScope io;
    assert(journal.BeginReservedPresentation(outcome.operationEpoch, outcome.operationId.data(), outcome.slot, outcome.physicalFingerprint));
    assert(journal.ResolveRestored(outcome.slot));
    assert(journal.ConfirmTimedPresentationScoped(outcome.operationEpoch, outcome.operationId.data(), outcome.slot, outcome.physicalFingerprint));
  }
  void Run() { IoScope io; assert(owner.RunOne()); }
  bool Pump() { ControlScope control; return bridge.Pump(); }
  Bridge::Handle Reserved(const Outcome& outcome) {
    { ControlScope control; assert(bridge.Bootstrap()); }
    ReserveJournal(outcome); Bridge::Handle handle;
    { ControlScope control; assert(bridge.Reserve(outcome, handle) == Transfer::RetainedVolatile); }
    assert(Pump()); Run(); assert(Pump());
    assert(bridge.PollReserve(handle) == ReserveStatus::DurableReserved);
    Pump(); assert(!owner.Busy());
    return handle;
  }
  void Completed(Bridge::Handle handle, const Outcome& outcome, uint64_t when = 500) {
    Physical(outcome);
    { ControlScope control; assert(bridge.Complete(handle, when) == Transfer::RetainedVolatile); }
    assert(Pump()); Run(); assert(Pump());
    assert(bridge.PollComplete(handle) == Status::Succeeded);
    Pump();
  }
};

void BootstrapDoesNotInventEmptyOrAuthority() {
  IoScope construction;
  Storage journalStorage, onlineStorage, offlineStorage;
  PersistentCommandJournal journal(journalStorage);
  KeepsakeCommittedInventory online(onlineStorage);
  kufr::offline::OfflineCommittedInventory offline(offlineStorage);
  JournalTransactionArbiter arbiter; Gate gate;
  Owner owner(journal, online, offline, arbiter, &gate); Bridge bridge(owner, gate);
  const auto original = Original(); Bridge::Handle handle; Outbox state;
  { ControlScope control;
    assert(!bridge.Bootstrap() && !bridge.Read(state));
    assert(bridge.Reserve(original, handle) == Transfer::Rejected && !handle.generation);
    assert(!bridge.IsPoisoned());
  }
}

void EightSlotsMailboxAndOriginalEnvelope() {
  Fixture fixture; assert(fixture.bridge.Bootstrap());
  Owner::Token other;
  assert(fixture.owner.TrySubmit(Enabled(false), other));
  const auto original = Original(); fixture.ReserveJournal(original);
  std::array<Bridge::Handle, Bridge::Capacity> handles{};
  std::array<Outcome, Bridge::Capacity> originals{};
  for (size_t index = 0; index < originals.size(); ++index) originals[index] = Original(static_cast<unsigned>(index + 1));
  const auto ninth = Original(9), replacement = Original(20);
  { ControlScope control;
    for (size_t index = 0; index < handles.size(); ++index)
      assert(fixture.bridge.Reserve(originals[index], handles[index]) == Transfer::RetainedVolatile);
    Bridge::Handle extra;
    assert(fixture.bridge.Reserve(ninth, extra) == Transfer::Busy && !extra.generation);
    assert(fixture.bridge.Reserve(original, handles[0]) == Transfer::AlreadyRetained);
    auto changed = original; ++changed.startedAtMs;
    assert(fixture.bridge.Reserve(changed, handles[0]) == Transfer::Rejected);
    changed = original; ++changed.physicalFingerprint;
    assert(fixture.bridge.Reserve(changed, handles[0]) == Transfer::Rejected);
    assert(!fixture.bridge.Pump());
    assert(fixture.bridge.PollReserve(handles[0]) == ReserveStatus::Pending);
    assert(fixture.bridge.Cancel(handles[7]));
    assert(fixture.bridge.Release(handles[7]));
    assert(fixture.bridge.Reserve(replacement, extra) == Transfer::RetainedVolatile);
    assert(extra.slot == handles[7].slot && extra.generation != handles[7].generation);
    assert(!fixture.bridge.Cancel(handles[7]));
  }
  fixture.Run(); Owner::Completion result;
  assert(fixture.owner.TryReadCompletion(other, result) && fixture.owner.ReleaseCompletion(other));
  assert(fixture.Pump()); fixture.Run(); assert(fixture.Pump());
  assert(fixture.bridge.PollReserve(handles[0]) == ReserveStatus::DurableReserved);
  assert(!fixture.arbiter.TryEnter()); // Result remains leased after adoption.
  assert(fixture.bridge.Occupied() == 8);
}

void CompletionTimestampAckAndCapacity() {
  Fixture fixture; const auto original = Original();
  const auto handle = fixture.Reserved(original);
  assert(fixture.bridge.Ack(handle, original) == Transfer::Rejected);
  fixture.Physical(original);
  { ControlScope control;
    assert(fixture.bridge.Complete(handle, 700) == Transfer::RetainedVolatile);
    assert(fixture.bridge.Complete(handle, 700) == Transfer::AlreadyRetained);
    assert(fixture.bridge.Complete(handle, 701) == Transfer::Rejected);
  }
  assert(fixture.Pump()); fixture.Run(); assert(fixture.Pump()); fixture.Pump();
  Outbox state; assert(fixture.bridge.Read(state));
  assert(state.records[0].completed && state.records[0].completedAtMs == 700);
  auto wrong = state.records[0]; ++wrong.completedAtMs;
  assert(fixture.bridge.Ack(handle, wrong) == Transfer::Rejected);
  wrong = state.records[0]; ++wrong.operationEpoch;
  assert(fixture.bridge.Ack(handle, wrong) == Transfer::Rejected);
  { ControlScope control;
    assert(fixture.bridge.Ack(handle, state.records[0]) == Transfer::RetainedVolatile);
    assert(fixture.bridge.Ack(handle, state.records[0]) == Transfer::AlreadyRetained);
  }
  assert(fixture.Pump()); fixture.Run();
  assert(!fixture.bridge.Release(handle));
  assert(fixture.Pump()); assert(fixture.bridge.PollAck(handle) == Status::Succeeded);
  assert(!fixture.bridge.Release(handle) && !fixture.arbiter.TryEnter());
  fixture.Pump(); assert(fixture.bridge.Release(handle));
  assert(fixture.bridge.Occupied() == 0);
}

void StopBeforeAndDuringReserve() {
  for (bool duringWrite : {false, true}) {
    Fixture fixture; const auto original = Original();
    assert(fixture.bridge.Bootstrap()); fixture.ReserveJournal(original);
    Bridge::Handle handle;
    assert(fixture.bridge.Reserve(original, handle) == Transfer::RetainedVolatile);
    assert(fixture.Pump());
    if (duringWrite) fixture.offlineStorage.duringWrite = [&] {
      ControlScope control; fixture.arbiter.CancelAdmissions(); assert(fixture.bridge.Cancel(handle));
    };
    else { fixture.arbiter.CancelAdmissions(); assert(fixture.bridge.Cancel(handle)); }
    fixture.Run(); fixture.offlineStorage.duringWrite = {};
    assert(fixture.Pump()); fixture.Pump();
    if (duringWrite) {
      assert(fixture.bridge.PollReserve(handle) == ReserveStatus::Refused);
      assert(!fixture.bridge.Release(handle) && fixture.bridge.Occupied() == 1);
      Outbox state; assert(fixture.bridge.Read(state) && state.records[0].operationId[0]);
      assert(fixture.bridge.Ack(handle, state.records[0]) == Transfer::Rejected);
    } else {
      assert(fixture.bridge.PollReserve(handle) == ReserveStatus::CancelledBeforeWrite);
      assert(fixture.bridge.Release(handle));
    }
  }
}

void LateStopStillPersistsRealCompletion() {
  Fixture fixture; const auto original = Original(); const auto handle = fixture.Reserved(original);
  fixture.Physical(original); fixture.arbiter.CancelAdmissions(); assert(fixture.bridge.Cancel(handle));
  assert(fixture.bridge.Complete(handle, 900) == Transfer::RetainedVolatile);
  assert(fixture.Pump()); fixture.Run(); assert(fixture.Pump()); fixture.Pump();
  assert(fixture.bridge.PollComplete(handle) == Status::Succeeded);
  assert(fixture.bridge.PollReserve(handle) == ReserveStatus::Refused);
  assert(!fixture.bridge.Release(handle));
}

void ImportedCompletedAndLegacyRows() {
  Fixture fixture; Outbox state;
  state.records[0] = Original(); state.records[0].completed = true; state.records[0].completedAtMs = 600;
  state.records[1] = Original(2); state.records[1].operationEpoch = state.records[1].physicalFingerprint = 0;
  { IoScope io; assert(fixture.offline.Write(state)); assert(fixture.owner.OpenAndPublish()); }
  assert(fixture.bridge.Bootstrap());
  Bridge::Handle completed, legacy;
  { ControlScope control;
    assert(fixture.bridge.Find(state.records[0], completed));
    assert(fixture.bridge.Find(state.records[1], legacy));
    assert(fixture.bridge.PollReserve(completed) == ReserveStatus::Refused);
    assert(fixture.bridge.Ack(legacy, state.records[1]) == Transfer::Rejected);
    assert(!fixture.bridge.Release(legacy));
    assert(fixture.bridge.Ack(completed, state.records[0]) == Transfer::RetainedVolatile);
  }
  assert(fixture.Pump()); fixture.Run(); assert(fixture.Pump()); fixture.Pump();
  assert(fixture.bridge.Release(completed));
  assert(fixture.bridge.Read(state) && state.records[1].operationId[0] && !state.records[0].operationId[0]);
  assert(fixture.bridge.Occupied() == 1);
}

void LostAckPoisonsOldRuntime(bool after) {
  Fixture fixture; const auto original = Original(); const auto handle = fixture.Reserved(original);
  fixture.Completed(handle, original);
  Outbox state; assert(fixture.bridge.Read(state));
  assert(fixture.bridge.Ack(handle, state.records[0]) == Transfer::RetainedVolatile);
  assert(fixture.Pump()); fixture.offlineStorage.cutBefore = !after; fixture.offlineStorage.cutAfter = after;
  fixture.Run(); assert(fixture.Pump()); fixture.Pump();
  assert(fixture.bridge.PollAck(handle) == Status::Unknown && fixture.bridge.IsPoisoned());
  assert(!fixture.bridge.Release(handle) && !fixture.bridge.Read(state) && !fixture.owner.Busy());
  fixture.offlineStorage.cutBefore = fixture.offlineStorage.cutAfter = false;
  { IoScope io; assert(fixture.owner.OpenAndPublish()); }
  assert(!fixture.bridge.Bootstrap() && fixture.bridge.Occupied() == 1);
  Bridge recovered(fixture.owner, fixture.gate); assert(recovered.Bootstrap() && recovered.Read(state));
  assert(bool(state.records[0].operationId[0]) == !after);
}

void SettingsBoundedPriorityAndPublicationAdoption() {
  Fixture fixture; assert(fixture.bridge.Bootstrap());
  auto first = Settings(1, true, false), second = Settings(2, true, false), third = Settings(3, true, false);
  const auto stop = Stop(1);
  Bridge::IntentHandle one, two, extra, urgent;
  { ControlScope control;
    assert(fixture.bridge.TryQueueSettings(Lane::Settings, first, one) == Transfer::RetainedVolatile);
    assert(fixture.bridge.TryQueueSettings(Lane::Settings, second, two) == Transfer::RetainedVolatile);
    assert(fixture.bridge.TryQueueSettings(Lane::Settings, third, extra) == Transfer::Busy && !extra.generation);
    assert(fixture.bridge.TryQueueSettings(Lane::Settings, first, one) == Transfer::AlreadyRetained);
    auto changed = first; changed.expectedStopRevision = 8;
    assert(fixture.bridge.TryQueueSettings(Lane::Settings, changed, one) == Transfer::Rejected);
    assert(fixture.bridge.TryQueueSettings(Lane::Stop, stop, urgent) == Transfer::RetainedVolatile);
    Outbox before; assert(fixture.bridge.Read(before) && before.serverStopRevision == 0 && before.serverSettingsRevision == 0);
    assert(fixture.bridge.Pump());
  }
  fixture.Run(); assert(fixture.Pump());
  assert(fixture.bridge.PollSettings(urgent) == Status::Succeeded);
  Outbox state; assert(fixture.bridge.Read(state) && state.serverStopRevision == 1 && state.serverSettingsRevision == 0);
  assert(!fixture.bridge.ReleaseSettings(urgent));
  assert(fixture.Pump()); // Release Stop lease and submit first stale fenced settings.
  assert(fixture.bridge.ReleaseSettings(urgent));
  fixture.Run(); assert(fixture.Pump());
  assert(fixture.bridge.PollSettings(one) == Status::Refused);
  assert(fixture.Pump()); assert(fixture.bridge.ReleaseSettings(one));
  fixture.Run(); assert(fixture.Pump()); fixture.Pump();
  assert(fixture.bridge.PollSettings(two) == Status::Refused && fixture.bridge.ReleaseSettings(two));
  first.expectedStopRevision = 1; first.serverSettingsRevision = 4;
  Bridge::IntentHandle fresh;
  assert(fixture.bridge.TryQueueSettings(Lane::Settings, first, fresh) == Transfer::RetainedVolatile);
  assert(fresh.slot == one.slot && fresh.generation != one.generation);
  assert(fixture.bridge.PollSettings(one) == Status::Refused && !fixture.bridge.ReleaseSettings(one));
  assert(fixture.Pump()); fixture.Run(); assert(fixture.Pump());
  assert(fixture.bridge.PollSettings(fresh) == Status::Succeeded);
  assert(fixture.bridge.Read(state) && state.serverSettingsRevision == 4 && state.serverStopRevision == 1);
}

void AcceptedRelaxationCannotRearmAfterStop() {
  Fixture fixture; assert(fixture.bridge.Bootstrap());
  auto enable = Enabled(true); Bridge::IntentHandle handle;
  assert(fixture.bridge.TryQueueSettings(Lane::Enabled, enable, handle) == Transfer::RetainedVolatile);
  assert(fixture.Pump());
  fixture.offlineStorage.duringWrite = [&] { fixture.arbiter.CancelAdmissions(); };
  fixture.Run(); fixture.offlineStorage.duringWrite = {};
  assert(fixture.Pump());
  assert(fixture.bridge.PollSettings(handle) == Status::Refused);
  Outbox state; assert(fixture.bridge.Read(state) && state.offlineEnabled); // Actual durable late write, no live authority.
  auto stop = Stop(2); Bridge::IntentHandle stopped;
  assert(fixture.bridge.TryQueueSettings(Lane::Stop, stop, stopped) == Transfer::RetainedVolatile);
  assert(fixture.Pump()); fixture.Run(); assert(fixture.Pump()); fixture.Pump();
  assert(fixture.bridge.PollSettings(stopped) == Status::Succeeded);
  assert(fixture.bridge.Read(state) && !state.offlineEnabled && state.quiet);
  assert(fixture.bridge.ReleaseSettings(handle) && fixture.bridge.ReleaseSettings(stopped));
}

void StopRefusesOlderUnsubmittedRelaxations() {
  Fixture fixture; assert(fixture.bridge.Bootstrap());
  Bridge::IntentHandle enabled, ambient, stopped;
  assert(fixture.bridge.TryQueueSettings(Lane::Enabled, Enabled(true), enabled) == Transfer::RetainedVolatile);
  assert(fixture.bridge.TryQueueSettings(Lane::Settings, Settings(1, false, true), ambient) == Transfer::RetainedVolatile);
  auto stop = Stop(0); // Local Stop has no server revision to fence stale work.
  assert(fixture.bridge.TryQueueSettings(Lane::Stop, stop, stopped) == Transfer::RetainedVolatile);
  assert(fixture.bridge.PollSettings(enabled) == Status::Refused);
  assert(fixture.bridge.PollSettings(ambient) == Status::Refused);
  fixture.arbiter.CancelAdmissions();
  assert(fixture.Pump()); fixture.Run(); assert(fixture.Pump()); fixture.Pump();
  assert(fixture.bridge.ReleaseSettings(stopped));
  assert(fixture.arbiter.ResumeAfterRecovery(fixture.arbiter.Cancellation()));
  assert(!fixture.Pump());
  Outbox state;
  assert(fixture.bridge.Read(state) && !state.offlineEnabled && !state.ambientEnabled && state.quiet);
  assert(fixture.bridge.PollSettings(enabled) == Status::Refused && fixture.bridge.ReleaseSettings(enabled));
  assert(fixture.bridge.PollSettings(ambient) == Status::Refused && fixture.bridge.ReleaseSettings(ambient));
  Bridge::IntentHandle safe;
  assert(fixture.bridge.TryQueueSettings(Lane::Enabled, Enabled(false), safe) == Transfer::RetainedVolatile);
  assert(fixture.Pump()); fixture.Run(); assert(fixture.Pump()); fixture.Pump();
  assert(fixture.bridge.PollSettings(safe) == Status::Succeeded && fixture.bridge.ReleaseSettings(safe));
}

void SettingsLostAcknowledgementHoldsIntent() {
  Fixture fixture; assert(fixture.bridge.Bootstrap());
  const auto settings = Settings(7, true, false);
  Bridge::IntentHandle handle;
  assert(fixture.bridge.TryQueueSettings(Lane::Settings, settings, handle) == Transfer::RetainedVolatile);
  assert(fixture.Pump()); fixture.offlineStorage.cutAfter = true;
  fixture.Run(); assert(fixture.Pump()); fixture.Pump();
  Outbox state;
  assert(fixture.bridge.PollSettings(handle) == Status::Unknown);
  assert(!fixture.bridge.Read(state) && !fixture.bridge.ReleaseSettings(handle));
  assert(fixture.bridge.HasWork() && !fixture.owner.Busy());
  fixture.offlineStorage.cutAfter = false;
  { IoScope io; assert(fixture.owner.OpenAndPublish()); }
  assert(!fixture.bridge.Bootstrap());
  Bridge authoritative(fixture.owner, fixture.gate);
  assert(authoritative.Bootstrap() && authoritative.Read(state));
  assert(state.serverSettingsRevision == 7);
}
}

int main() {
  BootstrapDoesNotInventEmptyOrAuthority();
  EightSlotsMailboxAndOriginalEnvelope();
  CompletionTimestampAckAndCapacity();
  StopBeforeAndDuringReserve(); LateStopStillPersistsRealCompletion();
  ImportedCompletedAndLegacyRows(); LostAckPoisonsOldRuntime(false); LostAckPoisonsOldRuntime(true);
  SettingsBoundedPriorityAndPublicationAdoption(); AcceptedRelaxationCannotRearmAfterStop();
  StopRefusesOlderUnsubmittedRelaxations();
  SettingsLostAcknowledgementHoldsIntent();
  std::cout << "offline evidence bridge checks passed\n";
}

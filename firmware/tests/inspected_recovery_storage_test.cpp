#include <algorithm>
#include <cassert>
#include <cstdio>
#include <iostream>
#include <memory>
#include <type_traits>
#include <vector>

#include "../src/features/device_control/DeviceStorageRuntime.h"
#include "physical_effects_test_support.h"

namespace {
using Runtime = DeviceControlRuntime;
using Progress = Runtime::RecoveryProgress;
using Handle = Runtime::RecoveryHandle;
constexpr uint8_t Slot = 3;
constexpr uint64_t Fingerprint = 77;
bool workerIo = false;
struct IoScope {
  bool previous = workerIo;
  IoScope() { workerIo = true; }
  ~IoScope() { workerIo = previous; }
};
std::string Id(unsigned value) {
  char text[37];
  std::snprintf(text, sizeof(text), "aaaaaaaa-aaaa-4aaa-8aaa-%012x", value);
  return text;
}
class Memory final : public JournalStorage {
 public:
  JournalReadResult Read(uint8_t* output, size_t capacity, size_t& length) override {
    assert(workerIo); ++reads; length = bytes.size();
    if (length > capacity) return JournalReadResult::Failed;
    if (!length) return JournalReadResult::Missing;
    std::copy(bytes.begin(), bytes.end(), output); return JournalReadResult::Found;
  }
  bool Write(const uint8_t* input, size_t length) override {
    assert(workerIo); ++writes;
    if (cutBefore) return false;
    bytes.assign(input, input + length); return !cutAfter;
  }
  std::vector<uint8_t> bytes;
  size_t reads = 0, writes = 0;
  bool cutBefore = false, cutAfter = false;
};
JournalIdentityView Identity(bool provisioned = true) {
  JournalIdentityView identity;
  identity.healthy = true; identity.provisioned = provisioned;
  if (provisioned) identity.deviceId = "inspection-device";
  return identity;
}
enum class Initial { Empty, Pending, OrphanedReference, MissingSource };
struct Durable {
  explicit Durable(Initial initial = Initial::Pending) {
    IoScope io;
    PersistentCommandJournal core(journal);
    JournalIdentityOwner identity(marker, core);
    KeepsakeCommittedInventory a(online);
    kufr::offline::OfflineCommittedInventory b(offline);
    assert(identity.BeginFactory("inspection-device", Identity(false), true));
    assert(core.EstablishScopedIdentity("inspection-device", true));
    assert(a.Establish("inspection-device", true));
    assert(b.EstablishBinding("inspection-device", true));
    JournalEvidenceInventory ai, bi;
    assert(a.ReadCommitted(ai) && b.ReadCommitted(bi));
    assert(identity.Finalize(Identity(), ai, bi, true));
    assert(core.PersistReadyMask(63));
    if (initial == Initial::Pending) assert(core.BeginScoped(1, Id(1), Slot, Fingerprint));
    if (initial == Initial::OrphanedReference) {
      kufr::offline::Outbox state;
      auto& record = state.records[0];
      const auto operation = Id(999), boot = Id(900), evening = Id(800);
      std::copy(operation.begin(), operation.end(), record.operationId.begin());
      std::copy(boot.begin(), boot.end(), record.bootId.begin());
      std::copy(evening.begin(), evening.end(), record.localEveningId.begin());
      record.generation = 1; record.startedAtMs = 1; record.slot = Slot;
      record.operationEpoch = 1; record.physicalFingerprint = 999;
      assert(b.Write(state));
    }
    if (initial == Initial::MissingSource) offline.bytes.clear();
  }
  std::unique_ptr<DeviceStorageRuntime> Open(Initial initial) {
    IoScope io;
    auto runtime = std::make_unique<DeviceStorageRuntime>(journal, marker, online, offline, Identity(),
        DeviceStorageRuntime::Policy::BoundedCompaction);
    const bool ready = runtime->Boot();
    assert(ready == (initial == Initial::Empty));
    return runtime;
  }
  Memory journal, marker, online, offline;
};
struct Rig {
  explicit Rig(DeviceStorageRuntime& storage)
      : storage(storage), effects(io), safetyPort(effects), safety(safetyPort), sequence(safety, true),
        control("inspection-device", Id(900), sequence, storage.ControlJournal(), 1), state(false, 0, ""),
        runtime(control, sequence, state, "catalog"), physical(effects, runtime), presentation(physical) {
    calibration.fill({1100, 1900, 120});
    assert(effects.Configure(calibration, 1)); effects.Tick(0); effects.Tick(now);
    assert(effects.RecoverAfterInspection()); // The trusted physical caller inspects once.
    assert(runtime.BindPresentation(presentation));
    physical.BindClock(Clock, this); physical.BindBootIdentity(Id(900));
    servoBefore = io.servoWrites; vibrationBefore = io.vibrationWrites;
  }
  static uint64_t Clock(void* context) { return static_cast<Rig*>(context)->now; }
  bool Work() { IoScope scope; return storage.RunOne(); }
  void Pass() {
    now += 10; effects.Tick(now);
    SelectionStep step; runtime.Tick(now, step);
    physical.Tick(now);
  }
  Progress Finish(Handle& handle) {
    for (unsigned attempt = 0; attempt != 20; ++attempt) {
      const auto result = runtime.PollInspectedRecovery(handle);
      if (result != Progress::Pending) return result;
      Work(); Pass();
    }
    assert(false); return Progress::Unknown;
  }
  void NoMotion() const {
    assert(io.servoWrites == servoBefore && io.vibrationWrites == vibrationBefore);
    assert(!runtime.HasAutonomousPresentation() && !control.HasManualControl());
  }
  DeviceStorageRuntime& storage;
  FakeIo io;
  PhysicalEffects effects;
  SafetyAdapter safetyPort;
  SafetyController safety;
  SelectionSequence sequence;
  DeviceControl control;
  ConversationRuntimeState state;
  Runtime runtime;
  PhysicalPresentation physical;
  WandPresentation presentation;
  std::array<ServoCalibration, 6> calibration{};
  uint64_t now = 30;
  int servoBefore = 0, vibrationBefore = 0;
};

void OriginalResolveAndReconciliationArePolledOnce() {
  Durable durable; auto storage = durable.Open(Initial::Pending); Rig rig(*storage); Handle handle;
  static_assert(!std::is_copy_constructible<Handle>::value && !std::is_move_constructible<Handle>::value,
      "Inspection capability stays bound to its original address");
  const auto revision = rig.control.Revision();
  const auto writes = durable.journal.writes;
  assert(rig.runtime.BeginInspectedRecovery(handle) == Progress::Pending && handle.IsBound());
  assert(rig.runtime.HasRecoveryWork() && !rig.runtime.FinishInspectedRecovery(handle));
  for (unsigned poll = 0; poll != 5; ++poll) {
    assert(rig.runtime.PollInspectedRecovery(handle) == Progress::Pending);
    assert(rig.runtime.BeginInspectedRecovery(handle) == Progress::Pending);
  }
  assert(durable.journal.writes == writes); rig.NoMotion();
  assert(rig.Work());
  storage->ControlJournal().PollStorage(); // Early publication adoption cannot consume the original result.
  assert(!storage->ControlJournal().HasPending() && storage->HasPendingJournalWork());
  assert(rig.runtime.PollInspectedRecovery(handle) == Progress::Pending);
  assert(!storage->HasPendingJournalWork() && rig.control.Revision() == revision);
  assert(rig.runtime.PollInspectedRecovery(handle) == Progress::Pending); // Reconciliation has not run.
  assert(rig.Work());
  assert(rig.runtime.PollInspectedRecovery(handle) == Progress::Succeeded);
  const auto completedRevision = rig.control.Revision();
  assert(completedRevision == revision + 1 && rig.control.ReadyMask() == (63u & ~(1u << (Slot - 1))));
  for (unsigned poll = 0; poll != 5; ++poll) assert(rig.runtime.PollInspectedRecovery(handle) == Progress::Succeeded);
  assert(rig.control.Revision() == completedRevision && durable.journal.writes == writes + 1);
  assert(!rig.runtime.HasRecoveryWork() && rig.runtime.FinishInspectedRecovery(handle) && !handle.IsBound());
  assert(rig.runtime.PollInspectedRecovery(handle) == Progress::Rejected);
  assert(!storage->ControlJournal().WasTimedPresentationCompletedHistorical(1, Id(1), Slot, Fingerprint));
  rig.NoMotion();
}

void StopBeforeOrAfterResolveAckRevokesOriginalGrant() {
  for (bool afterWrite : {false, true}) {
    Durable durable; auto storage = durable.Open(Initial::Pending); Rig rig(*storage); Handle handle;
    assert(rig.runtime.BeginInspectedRecovery(handle) == Progress::Pending);
    if (afterWrite) assert(rig.Work());
    SelectionStep step; rig.runtime.Stop(step);
    assert(rig.runtime.PollInspectedRecovery(handle) == Progress::Cancelled);
    assert(rig.runtime.BeginInspectedRecovery(handle) == Progress::Cancelled);
    if (!afterWrite) assert(rig.Work());
    for (unsigned pass = 0; pass != 4; ++pass) { rig.Pass(); rig.Work(); }
    assert(!storage->ControlJournal().HasPendingStorage());
    assert(!storage->ControlJournal().HasPending()); // A late recovery write is still adopted.
    assert(!storage->ControlJournal().CanBeginOperation() && rig.runtime.NeedsRecovery());
    assert(!rig.runtime.HasRecoveryWork() && rig.runtime.FinishInspectedRecovery(handle));
    assert(rig.effects.RecoverAfterInspection()); // A fresh inspection precedes a fresh capability.
    const auto restarted = rig.runtime.BeginInspectedRecovery(handle);
    assert(restarted == Progress::Pending || restarted == Progress::Succeeded);
    assert(rig.Finish(handle) == Progress::Succeeded);
    assert(rig.control.ReadyMask() == (63u & ~(1u << (Slot - 1))));
    rig.NoMotion();
  }
}

void CancelDetachesObserverOnlyAfterRevocation() {
  Durable durable; auto storage = durable.Open(Initial::Pending); Rig rig(*storage); Handle handle, other;
  assert(rig.runtime.BeginInspectedRecovery(handle) == Progress::Pending);
  assert(rig.runtime.BeginInspectedRecovery(other) == Progress::Rejected && !other.IsBound());
  rig.runtime.CancelInspectedRecovery(handle);
  assert(rig.runtime.PollInspectedRecovery(handle) == Progress::Cancelled);
  assert(rig.runtime.FinishInspectedRecovery(handle)); // The facade still owns the abandoned result.
  assert(rig.runtime.HasRecoveryWork());
  rig.Work(); rig.Pass();
  assert(!rig.runtime.HasRecoveryWork() && !storage->ControlJournal().HasPendingStorage());
  assert(!storage->ControlJournal().CanBeginOperation()); rig.NoMotion();
}

void StopDuringQueuedReconciliationCannotResumeTheOldFence() {
  Durable durable; auto storage = durable.Open(Initial::Pending); Rig rig(*storage); Handle handle;
  assert(rig.runtime.BeginInspectedRecovery(handle) == Progress::Pending);
  assert(rig.Work());
  assert(rig.runtime.PollInspectedRecovery(handle) == Progress::Pending); // Resolve ACK consumed; reconciliation requested.
  assert(!storage->HasPendingJournalWork());
  SelectionStep step; rig.runtime.Stop(step);
  for (unsigned pass = 0; pass != 4; ++pass) { rig.Work(); rig.Pass(); }
  assert(rig.runtime.PollInspectedRecovery(handle) == Progress::Cancelled);
  assert(!storage->ControlJournal().CanBeginOperation() && rig.runtime.NeedsRecovery());
  assert(rig.runtime.FinishInspectedRecovery(handle));
  rig.NoMotion();
}

void SourceFailureDuringReconciliationIsTerminal() {
  Durable durable; auto storage = durable.Open(Initial::Pending); Rig rig(*storage); Handle handle;
  assert(rig.runtime.BeginInspectedRecovery(handle) == Progress::Pending);
  assert(rig.Work());
  assert(rig.runtime.PollInspectedRecovery(handle) == Progress::Pending);
  durable.online.bytes.clear(); // Actual authority disappears before the worker's reconciliation read.
  rig.Work();
  assert(rig.runtime.PollInspectedRecovery(handle) == Progress::Unknown);
  assert(!rig.runtime.HasRecoveryWork() && !storage->AllowsRecovery());
  assert(rig.runtime.FinishInspectedRecovery(handle));
  rig.NoMotion();
}

void ForeignHandleCannotPollOrRestartAnotherRuntime() {
  Durable firstDurable, secondDurable;
  auto firstStorage = firstDurable.Open(Initial::Pending), secondStorage = secondDurable.Open(Initial::Pending);
  Rig first(*firstStorage), second(*secondStorage); Handle handle;
  assert(first.runtime.BeginInspectedRecovery(handle) == Progress::Pending);
  assert(second.runtime.BeginInspectedRecovery(handle) == Progress::Rejected);
  assert(second.runtime.PollInspectedRecovery(handle) == Progress::Rejected);
  second.runtime.CancelInspectedRecovery(handle);
  assert(!second.runtime.FinishInspectedRecovery(handle));
  assert(first.Finish(handle) == Progress::Succeeded);
  const auto revision = first.control.Revision();
  assert(first.runtime.BeginInspectedRecovery(handle) == Progress::Succeeded);
  assert(first.control.Revision() == revision); // Same bound capability observes its terminal result.
  assert(first.runtime.FinishInspectedRecovery(handle));
  assert(first.runtime.BeginInspectedRecovery(handle) == Progress::Succeeded); // Explicit new inspection.
  assert(first.control.Revision() == revision + 1);
  first.NoMotion(); second.NoMotion();
}

void ResumeWaitsForActualOutstandingSourceLease() {
  Durable durable(Initial::Empty); auto storage = durable.Open(Initial::Empty); Rig rig(*storage); Handle handle;
  EvidenceStorageOwner::Request request;
  request.operation = EvidenceStorageOwner::Operation::OfflineSettings;
  request.setQuiet = request.quiet = true;
  EvidenceStorageOwner::Token token;
  assert(storage->EvidenceRequests().TrySubmit(request, token));
  assert(rig.runtime.BeginInspectedRecovery(handle) == Progress::Pending);
  assert(rig.Work());
  assert(rig.runtime.PollInspectedRecovery(handle) == Progress::Pending); // Completed source result still owns its lease.
  EvidenceStorageOwner::Completion result;
  assert(storage->EvidenceRequests().TryReadCompletion(token, result));
  assert(result.status == EvidenceStorageOwner::Status::Succeeded);
  assert(storage->EvidenceRequests().ReleaseCompletion(token));
  assert(rig.Finish(handle) == Progress::Succeeded);
  rig.NoMotion();
}

void ManualLeaseExpiryRevokesTheOriginalRecovery() {
  Durable durable(Initial::Empty); auto storage = durable.Open(Initial::Empty); Rig rig(*storage); Handle initial, recovery;
  assert(rig.runtime.BeginInspectedRecovery(initial) == Progress::Succeeded);
  assert(rig.runtime.FinishInspectedRecovery(initial));
  assert(rig.runtime.AcquireManualControl("inspection-lease", rig.now));
  EvidenceStorageOwner::Request request;
  request.operation = EvidenceStorageOwner::Operation::OfflineSettings;
  request.setQuiet = request.quiet = true;
  EvidenceStorageOwner::Token token;
  assert(storage->EvidenceRequests().TrySubmit(request, token));
  assert(rig.runtime.BeginInspectedRecovery(recovery) == Progress::Pending);
  const auto generation = rig.runtime.Generation();
  rig.now += DeviceControl::ManualLeaseDurationMs;
  rig.Pass();
  assert(rig.runtime.Generation() > generation && !rig.control.HasManualControl());
  assert(rig.runtime.PollInspectedRecovery(recovery) == Progress::Cancelled);
  assert(rig.Work());
  EvidenceStorageOwner::Completion result;
  assert(storage->EvidenceRequests().TryReadCompletion(token, result));
  assert(storage->EvidenceRequests().ReleaseCompletion(token));
  for (unsigned pass = 0; pass != 4; ++pass) { rig.Work(); rig.Pass(); }
  assert(rig.runtime.PollInspectedRecovery(recovery) == Progress::Cancelled);
  assert(!storage->ControlJournal().CanBeginOperation());
  assert(rig.runtime.FinishInspectedRecovery(recovery));
  rig.NoMotion();
}

void LostResolveAckNeverReopensCurrentRuntime() {
  for (bool afterWrite : {false, true}) {
    Durable durable; auto storage = durable.Open(Initial::Pending); Rig rig(*storage); Handle handle;
    assert(rig.runtime.BeginInspectedRecovery(handle) == Progress::Pending);
    durable.journal.cutBefore = !afterWrite; durable.journal.cutAfter = afterWrite;
    assert(rig.Work());
    assert(rig.runtime.PollInspectedRecovery(handle) == Progress::Unknown);
    assert(!rig.runtime.HasRecoveryWork() && storage->CurrentState() == DeviceStorageRuntime::State::InventoryUnavailable);
    assert(!storage->ControlJournal().HasPendingStorage());
    const auto writes = durable.journal.writes;
    durable.journal.cutBefore = durable.journal.cutAfter = false;
    assert(rig.runtime.PollInspectedRecovery(handle) == Progress::Unknown);
    assert(rig.runtime.FinishInspectedRecovery(handle));
    assert(rig.runtime.BeginInspectedRecovery(handle) == Progress::Unknown);
    assert(durable.journal.writes == writes && !storage->AllowsRecovery());
    rig.NoMotion();
  }
}

void BindingAndReconciliationFailuresAreTerminal() {
  for (const auto initial : {Initial::MissingSource, Initial::OrphanedReference}) {
    Durable durable(initial); auto storage = durable.Open(initial); Rig rig(*storage); Handle handle;
    const auto expected = initial == Initial::MissingSource ? Progress::Unknown : Progress::Rejected;
    assert(rig.runtime.BeginInspectedRecovery(handle) == expected);
    assert(rig.runtime.PollInspectedRecovery(handle) == expected && !rig.runtime.HasRecoveryWork());
    assert(rig.runtime.NeedsRecovery() && rig.runtime.FinishInspectedRecovery(handle));
    rig.NoMotion();
  }
}

void TypedMailboxBusyIsNotRequestRefusal() {
  IoScope io;
  Memory bytes; PersistentCommandJournal core(bytes);
  assert(core.EstablishScopedIdentity("inspection-device", true) && core.PersistReadyMask(63));
  assert(core.BeginScoped(1, Id(1), Slot, Fingerprint));
  JournalTransactionArbiter arbiter;
  JournalStorageOwner owner(core, arbiter);
  JournalStorageOwner::Request request; request.operation = JournalStorageOwner::Operation::Resolve; request.readyMask = 59;
  JournalStorageOwner::Token token, busy;
  assert(owner.TrySubmitTyped(request, JournalStorageOwner::Purpose::Recovery, token) == JournalStorageOwner::Submission::Accepted);
  assert(owner.TrySubmitTyped(request, JournalStorageOwner::Purpose::Recovery, busy) == JournalStorageOwner::Submission::Busy);
  auto invalid = request; invalid.readyMask = 128;
  assert(owner.TrySubmitTyped(invalid, JournalStorageOwner::Purpose::Recovery, busy) == JournalStorageOwner::Submission::Rejected);
  assert(owner.RunOne()); JournalStorageOwner::Completion result;
  assert(owner.TryReadCompletion(token, result) && owner.ReleaseCompletion(token));
  assert(result.status == JournalStorageOwner::Status::Succeeded);
}

void FacadeRetainsBusyRecoveryFenceAndDistinguishesRefusal() {
  Memory bytes;
  {
    IoScope scope;
    PersistentCommandJournal provision(bytes);
    assert(provision.EstablishScopedIdentity("inspection-device", true) && provision.PersistReadyMask(63));
    assert(provision.BeginScoped(1, Id(1), Slot, Fingerprint));
  }
  std::unique_ptr<PersistentCommandJournal> core;
  { IoScope scope; core = std::make_unique<PersistentCommandJournal>(bytes); }
  JournalTransactionArbiter arbiter;
  JournalStorageOwner owner(*core, arbiter);
  AsyncCommandJournal facade(owner, arbiter);
  JournalStorageOwner::Request blocking;
  blocking.operation = JournalStorageOwner::Operation::Resolve; blocking.readyMask = 59;
  JournalStorageOwner::Token token;
  assert(owner.TrySubmitTyped(blocking, JournalStorageOwner::Purpose::Recovery, token) == JournalStorageOwner::Submission::Accepted);
  facade.BeginStorageRecovery();
  const auto writes = bytes.writes;
  assert(facade.ResolveStorageRecovery(59) == Progress::Pending); // Busy means retained retry, not refusal.
  assert(bytes.writes == writes);
  { IoScope scope; assert(owner.RunOne()); }
  JournalStorageOwner::Completion result;
  assert(owner.TryReadCompletion(token, result) && owner.ReleaseCompletion(token));
  assert(facade.ResolveStorageRecovery(128) == Progress::Rejected); // Invalid immutable request is not Pending.
  arbiter.CancelAdmissions();
  assert(facade.ResolveStorageRecovery(59) == Progress::Cancelled); // Cannot restamp the earlier inspection fence.
  assert(!facade.HasRecoveryStorageWork());
}
}

int main() {
  OriginalResolveAndReconciliationArePolledOnce();
  StopBeforeOrAfterResolveAckRevokesOriginalGrant();
  CancelDetachesObserverOnlyAfterRevocation();
  StopDuringQueuedReconciliationCannotResumeTheOldFence();
  SourceFailureDuringReconciliationIsTerminal();
  ForeignHandleCannotPollOrRestartAnotherRuntime();
  ResumeWaitsForActualOutstandingSourceLease();
  ManualLeaseExpiryRevokesTheOriginalRecovery();
  LostResolveAckNeverReopensCurrentRuntime();
  BindingAndReconciliationFailuresAreTerminal();
  TypedMailboxBusyIsNotRequestRefusal();
  FacadeRetainsBusyRecoveryFenceAndDistinguishesRefusal();
  std::cout << "inspected recovery storage checks passed\n";
}

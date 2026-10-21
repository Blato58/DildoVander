#include <cassert>
#include <algorithm>
#include <vector>
#include <condition_variable>
#include <future>
#include <thread>
#include <chrono>
#include "../src/features/voice_turn/LocalVoicePort.h"

#include "../src/features/device_control/DeviceControlRuntime.h"
#include "../src/features/device_control/AsyncCommandJournal.h"
#include "../src/features/device_control/PersistentCommandJournal.h"
#include "../src/features/local_control/LocalControlOwner.h"
#include "../src/features/local_control/LocalControlTransport.h"
#include "../src/features/local_control/LocalConversationNetworkPort.h"
#include "../src/features/local_control/LocalConversationReplyPort.h"

namespace {

constexpr char Boot[] = "c85743d5-1b72-41b8-8500-ae3c102b27d6";
constexpr char Lease[] = "a8287a95-b6a1-4501-bdaa-38721ec83191";
constexpr char Conversation[] = "ac665612-864c-4482-bb68-7415921119ae";
constexpr char Turn[] = "8475532e-92b2-487e-b476-7f98f0030e68";
constexpr char Command[] = "3d6104de-98c6-40b5-b10d-4f5675a63c51";

class Safety final : public SafetyPort {
 public:
  void DisableActuators() override { ++disabled; }
  void StartVibration(uint8_t) override { ++vibrations; }
  void ReleaseLatch(uint8_t) override {}
  int disabled = 0;
  int vibrations = 0;
};

class Journal final : public CommandJournal {
 public:
  bool HasPending() const override { return pending; }
  uint8_t PendingCompartment() const override { return pending ? 1 : 0; }
  uint8_t ReadyMask() const override { return 63; }
  bool Begin(const std::string&, uint8_t) override {
    pending = true;
    ++begins;
    return true;
  }
  bool Resolve(uint8_t) override {
    pending = false;
    return true;
  }
  bool PersistReadyMask(uint8_t) override { return true; }
  bool pending = false;
  int begins = 0;
};

class Network final : public LocalConversationNetworkPort {
 public:
  bool QueueTextTurn(const std::string&, const std::string&,
                     const std::string&,
                     const ConversationRuntimeSnapshot&) override {
    if (accept) {
      ++queued;
    }
    return accept;
  }
  bool QueueCancelFor(const std::string&, uint64_t) override {
    ++cancels;
    return true;
  }
  bool TakeFailedTurnFor(const std::string&, const std::string&,
                         uint64_t) override {
    const bool result = failed;
    failed = false;
    return result;
  }
  bool accept = true;
  bool failed = false;
  int queued = 0;
  int cancels = 0;
};

class Transport final : public LocalControlTransport {
 public:
  explicit Transport(LocalControlInbox& inbox) : inbox(inbox) {}
  bool Send(const LocalSocketAdmission::Handle&, const std::string&) override {
    return true;
  }
  void Close(const LocalSocketAdmission::Handle& handle) override {
    if (handle.socket >= 0) {
      ++closes;
      inbox.Disconnect(handle);
    }
  }
  LocalControlInbox& inbox;
  int closes = 0;
};

class Clock final : public LocalControlClockPort {
 public:
  uint64_t NowMs() const override { return now; }
  uint64_t now = 100;
};

class Output final : public LocalControlOutputPort {
 public:
  Output(LocalControlInbox& inbox, Clock& clock) : inbox(inbox), clock(clock) {}
  bool ControlAck(const LocalSocketAdmission::Handle&,
                  const LocalControlMessage& message, LocalControlError error) override {
    ++controlAcks;
    lastControlKind = message.kind;
    lastControlError = error;
    return accept;
  }
  bool VoiceAck(const LocalSocketAdmission::Handle&, const LocalControlMessage&, bool admitted) override {
    lastVoiceAccepted = admitted; return accept;
  }
  bool lastVoiceAccepted = false;
  bool DanceAck(const LocalSocketAdmission::Handle&, const LocalControlMessage&, bool admitted) override {
    lastDanceAccepted = admitted; return accept;
  }
  bool lastDanceAccepted = false;
  bool Welcome(const LocalSocketAdmission::Handle&, const std::string&,
                uint64_t) override { ++welcomes; return accept; }
  bool Snapshot(const LocalSocketAdmission::Handle&, const std::string& lease,
                 uint64_t) override {
    ++snapshots;
    lastLease = lease;
    return accept;
  }
  bool ScopedSnapshot(const LocalSocketAdmission::Handle& handle, const std::string& lease,
                      uint64_t nowMs, bool includeOperationEpoch) override {
    scopedEpoch = includeOperationEpoch;
    return Snapshot(handle, lease, nowMs);
  }
  bool scopedEpoch = false;
  bool Heartbeat(const LocalSocketAdmission::Handle&,
                  const LocalControlMessage&, uint64_t) override {
    ++heartbeats;
    return accept;
  }
  bool CommandAck(const LocalSocketAdmission::Handle& handle,
                   const LocalControlMessage&, const CommandReceipt& receipt) override {
    ++commandAcks;
    lastCommand = receipt;
    if (delayAck) {
      clock.now = delayedAckAt;
    }
    if (injectStop) {
      LocalControlMessage stop;
      stop.kind = LocalControlMessageKind::Stop;
      stop.deviceId = "kufr-001";
      stop.bootId = Boot;
      assert(inbox.Submit(handle, stop));
    }
    return accept;
  }
  bool ConversationAck(const LocalSocketAdmission::Handle&,
                        const LocalControlMessage&,
                        const ConversationCommandReceipt& receipt) override {
    lastConversation = receipt;
    return accept;
  }
  bool ConversationResult(const LocalSocketAdmission::Handle& handle,
                          const std::string&, uint64_t,
                          const std::string&) override {
    ++results;
    if (injectStop) {
      LocalControlMessage stop;
      stop.kind = LocalControlMessageKind::Stop;
      stop.deviceId = "kufr-001";
      stop.bootId = Boot;
      assert(inbox.Submit(handle, stop));
    }
    return accept;
  }
  LocalControlInbox& inbox;
  bool accept = true;
  bool injectStop = false;
  bool delayAck = false;
  uint64_t delayedAckAt = 7000;
  Clock& clock;
  int welcomes = 0;
  int snapshots = 0;
  int heartbeats = 0;
  int results = 0;
  int controlAcks = 0;
  int commandAcks = 0;
  LocalControlError lastControlError = LocalControlError::None;
  LocalControlMessageKind lastControlKind = LocalControlMessageKind::Hello;
  std::string lastLease;
  CommandReceipt lastCommand;
  ConversationCommandReceipt lastConversation;
};

class Replies final : public LocalConversationReplyPort {
 public:
  bool TakeReply(std::string& reply, std::string& conversationId,
                 std::string& clientTurnId,
                 ConversationRuntimeSnapshot& outputSnapshot,
                 uint64_t& token) override {
    if (!pending) {
      return false;
    }
    pending = false;
    reply = "Backend reply";
    conversationId = Conversation;
    clientTurnId = Turn;
    outputSnapshot = snapshot;
    token = 4;
    return true;
  }
  bool IsReplyCurrent(uint64_t token,
                      const ConversationRuntimeSnapshot&) const override {
    return current && token == 4;
  }
  bool FinishReply(uint64_t token) override {
    assert(token == 4);
    ++finished;
    return true;
  }
  ConversationRuntimeSnapshot snapshot;
  bool pending = true;
  bool current = true;
  int finished = 0;
};

class Ids final : public LocalControlIdPort {
 public:
  std::string NewLeaseId() override { return fail ? "" : Lease; }
  bool fail = false;
};

class Recovery final : public LocalControlRecoveryGate {
 public:
  bool InspectionAllowsRecovery() const override { return allow; }
  bool allow = false;
};

class Steps final : public LocalControlStepPort {
 public:
  void OnStep(const SelectionStep& step) override { effects.push_back(step.effect); }
  std::vector<SelectionEffect> effects;
};

class Voice final : public LocalVoicePort {
 public:
  LocalVoiceStatus VoiceStatus() const override { return status; }
  LocalVoiceStatus status;
  bool ConfigureVoice(const std::string& mode, const std::string& chosen) override {
    ++configured; return mode == "realtime" && chosen == "cedar";
  }
  bool StartVoice(const std::string&, const ConversationRuntimeSnapshot& snapshot) override {
    ++started; lastRevision = snapshot.deviceRevision; return true;
  }
  void StopVoice() override { ++stopped; }
  int configured = 0, started = 0, stopped = 0;
  uint64_t lastRevision = 0;
};

class Dance final : public kufr::dance::LocalDancePort {
 public:
  kufr::dance::DanceStatus DanceState() const override { return status; }
  bool StartDance(const std::string&) override { ++starts; status.phase = "starting"; return true; }
  void StopDance() override {
    if (kufr::dance::IsDanceActive(status)) { ++stops; status.phase = "stopping"; }
  }
  kufr::dance::DanceStatus status;
  int starts = 0, stops = 0;
};

struct SyncJournalContext { Journal journal; };
class AsyncStorage final : public JournalStorage {
 public:
  JournalReadResult Read(uint8_t* output, size_t capacity, size_t& length) override {
    length = bytes.size();
    if (bytes.empty()) return JournalReadResult::Missing;
    if (length > capacity) return JournalReadResult::Failed;
    std::copy(bytes.begin(), bytes.end(), output);
    return JournalReadResult::Found;
  }
  bool Write(const uint8_t* input, size_t length) override {
    if (block) {
      std::unique_lock<std::mutex> lock(mutex);
      entered = true;
      changed.notify_all();
      changed.wait(lock, [&]() { return released; });
    }
    if (cutBefore) return false;
    bytes.assign(input, input + length);
    return !cutAfter;
  }
  void AwaitWrite() {
    std::unique_lock<std::mutex> lock(mutex);
    assert(changed.wait_for(lock, std::chrono::seconds(3), [&]() { return entered; }));
  }
  void ReleaseWrite() {
    std::lock_guard<std::mutex> lock(mutex);
    released = true;
    changed.notify_all();
  }
  std::vector<uint8_t> bytes;
  bool block = false, cutBefore = false, cutAfter = false;
 private:
  std::mutex mutex;
  std::condition_variable changed;
  bool entered = false, released = false;
};
template <uint8_t InitialReadyMask>
struct AsyncJournalContextT {
  static PersistentCommandJournal& Initialize(PersistentCommandJournal& core) {
    assert(core.EstablishScopedIdentity("kufr-001", true));
    assert(core.PersistReadyMask(InitialReadyMask));
    return core;
  }
  AsyncJournalContextT() : core(storage), storageOwner(Initialize(core), arbiter), journal(storageOwner, arbiter) {}
  AsyncStorage storage;
  PersistentCommandJournal core;
  JournalTransactionArbiter arbiter;
  JournalStorageOwner storageOwner;
  AsyncCommandJournal journal;
};
template <class JournalContext>
struct BasicFixture : JournalContext {
  explicit BasicFixture(bool voiceAvailable = true, bool danceAvailable = true)
      : safety(port), sequence(safety),
        control("kufr-001", Boot, sequence, this->journal, 1), state(false, 0, ""),
        runtime(control, sequence, state, "neutral-1"),
        conversation(Boot, Conversation), inbox("kufr-001", Boot),
        transport(inbox), output(inbox, clock),
        owner("kufr-001", Boot, runtime, state, conversation, network,
              inbox, transport, output, ids, recovery, steps, clock,
              voiceAvailable ? &voice : nullptr, danceAvailable ? &dance : nullptr) {
    safety.InitializeSafe();
    assert(runtime.RecoverAfterInspection());
  }
  LocalControlMessage Message(LocalControlMessageKind kind) const {
    LocalControlMessage message;
    message.kind = kind;
    message.messageId = Command;
    message.commandId = Command;
    message.deviceId = "kufr-001";
    message.bootId = Boot;
    message.controlLeaseId = Lease;
    message.conversationId = Conversation;
    message.clientTurnId = Turn;
    message.generation = runtime.Generation();
    message.expectedRevision = control.Revision();
    message.sequence = 1;
    message.issuedAtDeviceMs = 100;
    message.maxAgeMs = 1000;
    message.compartment = 1;
    message.text = "Visitor text";
    return message;
  }
  void Submit(LocalControlMessageKind kind, uint64_t nowMs = 100) {
    assert(inbox.Submit(handle, Message(kind)));
    Poll(nowMs);
  }
  void Poll(uint64_t nowMs) {
    clock.now = nowMs;
    owner.Poll();
  }
  void Connect(bool hello = true) {
    assert(inbox.Open(handle, 100));
    Poll(100);
    if (hello) {
      Submit(LocalControlMessageKind::Hello);
    }
  }
  Safety port;
  SafetyController safety;
  SelectionSequence sequence;
  DeviceControl control;
  ConversationRuntimeState state;
  DeviceControlRuntime runtime;
  ConversationSession conversation;
  Network network;
  LocalControlInbox inbox;
  Transport transport;
  Clock clock;
  Output output;
  Ids ids;
  Recovery recovery;
  Steps steps;
  Voice voice;
  Dance dance;
  LocalControlOwner owner;
  LocalSocketAdmission::Handle handle{7, 1};
};
using Fixture = BasicFixture<SyncJournalContext>;
using AsyncFixture = BasicFixture<AsyncJournalContextT<63>>;
using AsyncReloadFixture = BasicFixture<AsyncJournalContextT<62>>;

template <class FixtureType>
LocalControlMessage ScopedManual(FixtureType& fixture, LocalControlMessageKind kind) {
  auto command = fixture.Message(kind);
  command.hasOperationEpoch = true;
  command.operationEpoch = 1;
  return command;
}
void QueueAsyncSelection(AsyncFixture& fixture) {
  fixture.Connect();
  fixture.Submit(LocalControlMessageKind::Acquire);
  assert(fixture.inbox.Submit(fixture.handle, ScopedManual(fixture, LocalControlMessageKind::Selection)));
  fixture.Poll(100);
  assert(fixture.output.commandAcks == 0 && !fixture.sequence.IsBusy());
  assert(fixture.control.HasStorageWork() && fixture.storageOwner.Busy());
}
void TestDeferredManualAckAndCoalescing() {
  AsyncFixture fixture;
  QueueAsyncSelection(fixture);
  auto command = ScopedManual(fixture, LocalControlMessageKind::Selection);
  assert(fixture.inbox.Submit(fixture.handle, command));
  fixture.Poll(100);
  assert(fixture.output.commandAcks == 0);
  ++command.compartment;
  assert(fixture.inbox.Submit(fixture.handle, command));
  fixture.Poll(100);
  assert(fixture.output.commandAcks == 1 && fixture.output.lastCommand.error == CommandError::IdConflict);
  fixture.Submit(LocalControlMessageKind::Cancel); // invalid conversation cancel cannot erase manual retry
  assert(fixture.storageOwner.RunOne());
  assert(!fixture.sequence.IsBusy()); // durable commit alone never starts motion
  fixture.owner.Tick();
  assert(fixture.output.commandAcks == 2 && fixture.output.lastCommand.error == CommandError::None);
  assert(fixture.output.lastCommand.outcome == CommandOutcome::Accepted);
  assert(fixture.sequence.IsBusy() && fixture.control.HasPendingSelection());
  assert(!fixture.journal.HasPendingStorage());
}
void TestDeferredManualAckFailureAndUrgentStop() {
  for (int refusal = 0; refusal < 4; ++refusal) {
    AsyncFixture fixture;
    QueueAsyncSelection(fixture);
    assert(fixture.storageOwner.RunOne());
    fixture.output.accept = refusal != 0;
    fixture.output.injectStop = refusal == 1;
    fixture.output.delayAck = refusal >= 2;
    if (refusal == 3) fixture.output.delayedAckAt = 1200; // TTL expired, original lease still live
    fixture.owner.Tick();
    assert(!fixture.sequence.IsBusy() && fixture.control.NeedsRecovery());
    assert(fixture.core.HasPending());
    assert(!fixture.core.WasTimedPresentationCompletedScoped(1, Command, 1, 0));
    assert(std::find(fixture.steps.effects.begin(), fixture.steps.effects.end(),
                     SelectionEffect::SearchStep) == fixture.steps.effects.end());
    fixture.owner.Tick();
    assert(!fixture.control.HasStorageWork());
  }
}
void TestStopWhileActualJournalWriteIsStalled() {
  AsyncFixture fixture;
  QueueAsyncSelection(fixture);
  fixture.storage.block = true;
  std::thread worker([&]() { assert(fixture.storageOwner.RunOne()); });
  fixture.storage.AwaitWrite();
  assert(fixture.inbox.Submit(fixture.handle, fixture.Message(LocalControlMessageKind::Stop)));
  auto stop = std::async(std::launch::async, [&]() { fixture.owner.Tick(); });
  const bool immediate = stop.wait_for(std::chrono::seconds(1)) == std::future_status::ready;
  fixture.storage.ReleaseWrite();
  worker.join();
  stop.get();
  assert(immediate && fixture.output.commandAcks == 0 && !fixture.sequence.IsBusy());
  fixture.owner.Tick();
  assert(fixture.core.HasPending() && fixture.journal.HasPending());
  assert(!fixture.control.HasStorageWork() && fixture.control.NeedsRecovery());
  assert((fixture.control.ReadyMask() & 1) == 0);
}
void TestDeferredManualLifetimeAndOriginalEpoch() {
  for (int reason = 0; reason < 4; ++reason) {
    AsyncFixture fixture;
    QueueAsyncSelection(fixture);
    if (reason == 0) fixture.clock.now = 1200; // original TTL expires, lease remains live
    if (reason == 1) fixture.clock.now = 6200; // original lease expires
    if (reason == 2) fixture.inbox.Disconnect(fixture.handle);
    if (reason == 3) fixture.owner.SetMaintenance(true);
    fixture.owner.Tick();
    assert(fixture.storageOwner.RunOne());
    fixture.owner.Tick();
    assert(!fixture.sequence.IsBusy() && fixture.control.NeedsRecovery());
    assert(!fixture.core.HasPending() && !fixture.control.HasStorageWork());
    assert(fixture.core.CurrentEpoch() == 1);
  }
}
void TestDeferredReloadHasNoEarlyAcknowledgement() {
  AsyncReloadFixture fixture;
  fixture.Connect();
  fixture.Submit(LocalControlMessageKind::Acquire);
  auto command = ScopedManual(fixture, LocalControlMessageKind::Reload);
  const auto revision = fixture.control.Revision();
  assert(fixture.inbox.Submit(fixture.handle, command));
  fixture.Poll(100);
  assert(fixture.output.commandAcks == 0 && fixture.control.Revision() == revision);
  assert(fixture.storageOwner.RunOne());
  fixture.owner.Tick();
  assert(fixture.output.commandAcks == 1 && fixture.output.lastCommand.error == CommandError::None);
  assert(fixture.control.Revision() > revision && !fixture.control.HasStorageWork());
  assert(!fixture.sequence.IsBusy());
}
void ReachDeferredPhysicalClose(AsyncFixture& fixture) {
  QueueAsyncSelection(fixture);
  assert(fixture.storageOwner.RunOne());
  fixture.owner.Tick();
  assert(fixture.sequence.IsBusy());
  for (uint64_t time = 150; time < 5000 && fixture.sequence.IsBusy(); time += 50) {
    fixture.clock.now = time;
    fixture.owner.Tick();
  }
  assert(!fixture.sequence.IsBusy() && fixture.control.HasPendingSelection());
  assert(fixture.control.HasStorageWork() && fixture.journal.HasPendingStorage());
  assert(fixture.control.LastResult().outcome != CommandOutcome::Completed);
}
void TestPhysicalCloseWaitsForDurableAck() {
  AsyncFixture fixture;
  ReachDeferredPhysicalClose(fixture);
  assert(fixture.storageOwner.RunOne());
  assert(fixture.control.LastResult().outcome != CommandOutcome::Completed);
  fixture.owner.Tick();
  assert(fixture.control.LastResult().outcome == CommandOutcome::Completed);
  assert(!fixture.control.HasPendingSelection() && !fixture.control.HasStorageWork());
  assert(!fixture.core.HasPending() && (fixture.control.ReadyMask() & 1) == 0);
}
void TestStopDuringPhysicalCloseAndInspectedRecovery() {
  AsyncFixture fixture;
  ReachDeferredPhysicalClose(fixture);
  fixture.Submit(LocalControlMessageKind::Stop, fixture.clock.now);
  assert(fixture.control.LastResult().outcome == CommandOutcome::OutcomeUnknown);
  assert(fixture.storageOwner.RunOne()); // completion evidence still closes its durable obligation
  fixture.owner.Tick();
  assert(!fixture.core.HasPending() && fixture.control.NeedsRecovery());
  assert(fixture.control.LastResult().outcome == CommandOutcome::OutcomeUnknown);
  assert(fixture.runtime.RecoverAfterInspection());
  assert(!fixture.control.NeedsRecovery() && !fixture.control.HasStorageWork());
}
void TestLateBeginRecoveryAndLaterStop() {
  AsyncFixture fixture;
  QueueAsyncSelection(fixture);
  assert(fixture.storageOwner.RunOne());
  fixture.Submit(LocalControlMessageKind::Stop);
  fixture.owner.Tick();
  assert(fixture.core.HasPending() && fixture.control.NeedsRecovery());
  assert(!fixture.runtime.RecoverAfterInspection());
  assert(fixture.journal.HasPendingStorage());
  fixture.Submit(LocalControlMessageKind::Stop); // cancels this inspected attempt too
  assert(fixture.storageOwner.RunOne());
  fixture.owner.Tick();
  assert(fixture.control.NeedsRecovery() && !fixture.control.HasStorageWork());
  assert(!fixture.core.HasPending());
  assert(fixture.runtime.RecoverAfterInspection()); // requires a fresh explicit inspection call
  assert(!fixture.control.NeedsRecovery() && !fixture.sequence.IsBusy());
}
void TestManualLostCommitAckFailsClosed() {
  for (bool afterWrite : {false, true}) {
    AsyncFixture fixture;
    QueueAsyncSelection(fixture);
    fixture.storage.cutAfter = afterWrite;
    fixture.storage.cutBefore = !afterWrite;
    assert(fixture.storageOwner.RunOne());
    fixture.owner.Tick();
    assert(fixture.output.commandAcks == 1 && fixture.output.lastCommand.error != CommandError::None);
    assert(!fixture.sequence.IsBusy() && fixture.control.NeedsRecovery());
    assert(!fixture.runtime.RecoverAfterInspection());
    assert(!fixture.core.IsHealthy());
    PersistentCommandJournal reopened(fixture.storage);
    assert(reopened.IsHealthy() && reopened.HasPending() == afterWrite);
  }
}

void TestHelloAndObserver() {
  Fixture fixture;
  fixture.Connect(false);
  fixture.Poll(10101);
  assert(fixture.transport.closes == 1 && fixture.output.welcomes == 0);
  fixture.Poll(10102);
  fixture.handle = {7, 2};
  fixture.Connect();
  assert(fixture.output.welcomes == 1);
  const uint64_t revision = fixture.control.Revision();
  const uint64_t generation = fixture.runtime.Generation();
  SelectionStep step;
  assert(fixture.sequence.Start(1, 100, SelectionControlMode::Standalone, step));
  fixture.inbox.Disconnect({7, 1});
  fixture.Submit(LocalControlMessageKind::StateGet, 101);
  fixture.inbox.Disconnect(fixture.handle);
  fixture.Poll(102);
  assert(fixture.sequence.IsBusy());
  assert(fixture.control.Revision() == revision);
  assert(fixture.runtime.Generation() == generation);

  Fixture beforeHello;
  beforeHello.Connect(false);
  beforeHello.Submit(LocalControlMessageKind::StateGet);
  assert(beforeHello.transport.closes == 1);
  Fixture repeated;
  repeated.Connect();
  repeated.Submit(LocalControlMessageKind::Hello);
  assert(repeated.transport.closes == 1);
}

void TestLegacyTextRequiresUpgrade() {
  Fixture fixture;
  fixture.Connect();
  fixture.Submit(LocalControlMessageKind::Acquire);
  assert(fixture.control.HasManualControl() && fixture.output.lastLease == Lease);
  fixture.Submit(LocalControlMessageKind::Text);
  assert(fixture.output.lastConversation.error == ConversationCommandError::EngineUpgradeRequired);
  assert(fixture.network.queued == 0 && !fixture.conversation.HasActiveTurn());
  fixture.Submit(LocalControlMessageKind::Text);
  assert(fixture.output.lastConversation.error == ConversationCommandError::EngineUpgradeRequired);
  assert(fixture.network.queued == 0 && !fixture.output.lastConversation.duplicate);
  fixture.Submit(LocalControlMessageKind::Heartbeat, 200);
  assert(fixture.output.heartbeats == 1 && fixture.control.HasManualControl());
  fixture.Submit(LocalControlMessageKind::Release, 201);
  assert(!fixture.control.HasManualControl());
  assert(fixture.runtime.Generation() == 1);
  Fixture failedOutput;
  failedOutput.Connect();
  failedOutput.Submit(LocalControlMessageKind::Acquire);
  failedOutput.output.accept = false;
  failedOutput.Submit(LocalControlMessageKind::Text);
  assert(failedOutput.network.queued == 0 && failedOutput.network.cancels == 0);
  assert(!failedOutput.control.HasManualControl());
  assert(!failedOutput.conversation.HasActiveTurn());
}

void TestExpiryCancelFailureAndReset() {
  Fixture expiry;
  expiry.Connect();
  expiry.Submit(LocalControlMessageKind::Acquire);
  expiry.Submit(LocalControlMessageKind::Text, 6100);
  assert(expiry.network.queued == 0 && !expiry.control.HasManualControl());
  assert(expiry.transport.closes == 1);
  Fixture blockedReset;
  blockedReset.Connect();
  blockedReset.Submit(LocalControlMessageKind::Acquire);
  blockedReset.Submit(LocalControlMessageKind::Stop);
  blockedReset.Submit(LocalControlMessageKind::Reset, 101);
  assert(blockedReset.control.NeedsRecovery() && blockedReset.transport.closes == 0);
  Fixture reset;
  reset.Connect();
  reset.Submit(LocalControlMessageKind::Acquire);
  reset.Submit(LocalControlMessageKind::Stop);
  reset.recovery.allow = true;
  reset.Submit(LocalControlMessageKind::Reset, 101);
  assert(!reset.control.NeedsRecovery() && reset.control.HasManualControl());
}

void TestStopBetweenAckAndBegin() {
  Fixture fixture;
  fixture.Connect();
  fixture.Submit(LocalControlMessageKind::Acquire);
  fixture.output.injectStop = true;
  fixture.Submit(LocalControlMessageKind::Selection);
  assert(fixture.output.lastCommand.error == CommandError::None);
  assert(fixture.journal.begins == 1 && fixture.journal.pending);
  assert(!fixture.sequence.IsBusy() && fixture.control.NeedsRecovery());
  assert(std::find(fixture.steps.effects.begin(), fixture.steps.effects.end(),
                   SelectionEffect::SearchStep) == fixture.steps.effects.end());
  assert(fixture.port.vibrations == 0);
  assert(!fixture.control.HasPendingSelection());
  Fixture delayed;
  delayed.Connect();
  delayed.Submit(LocalControlMessageKind::Acquire);
  delayed.output.delayAck = true;
  delayed.Submit(LocalControlMessageKind::Selection);
  assert(delayed.journal.begins == 1);
  assert(!delayed.control.HasManualControl());
  assert(!delayed.sequence.IsBusy() && delayed.control.NeedsRecovery());
  assert(std::find(delayed.steps.effects.begin(), delayed.steps.effects.end(),
                   SelectionEffect::SearchStep) == delayed.steps.effects.end());
  assert(std::find(delayed.steps.effects.begin(), delayed.steps.effects.end(),
                   SelectionEffect::Stopped) != delayed.steps.effects.end());
  Fixture full;
  full.Connect();
  full.Submit(LocalControlMessageKind::Acquire);
  for (size_t index = 0; index < LocalControlInbox::QueueCapacity; ++index) {
    assert(full.inbox.Submit(full.handle, full.Message(LocalControlMessageKind::Selection)));
  }
  assert(full.inbox.Submit(full.handle, full.Message(LocalControlMessageKind::Stop)));
  full.inbox.Disconnect(full.handle);
  full.Poll(101);
  assert(full.journal.begins == 0 && full.control.NeedsRecovery());
  assert(!full.control.HasManualControl());
  Fixture tick;
  tick.Connect();
  tick.Submit(LocalControlMessageKind::Acquire);
  tick.Submit(LocalControlMessageKind::Selection);
  assert(tick.sequence.IsBusy());
  assert(tick.inbox.Submit(tick.handle, tick.Message(LocalControlMessageKind::Stop)));
  tick.clock.now = 101;
  tick.owner.Tick();
  assert(!tick.sequence.IsBusy() && tick.port.vibrations == 0);
}

void TestRetiredTextHasNoReply() {
  Fixture fixture;
  fixture.Connect();
  fixture.Submit(LocalControlMessageKind::Acquire);
  fixture.Submit(LocalControlMessageKind::Text);
  Replies reply;
  assert(fixture.state.Read(reply.snapshot));
  fixture.owner.PumpReply(reply);
  assert(fixture.output.results == 0 && reply.finished == 1);
  assert(!fixture.conversation.HasActiveTurn());
}

void TestStandaloneVoice() {
  Fixture fixture;
  fixture.Connect();
  auto configure = fixture.Message(LocalControlMessageKind::VoiceConfigure);
  configure.voiceMode = "realtime"; configure.voice = "cedar";
  assert(fixture.inbox.Submit(fixture.handle, configure)); fixture.Poll(100);
  assert(fixture.output.lastVoiceAccepted && fixture.voice.configured == 1);
  auto start = fixture.Message(LocalControlMessageKind::VoiceStart);
  ++start.expectedRevision;
  assert(fixture.inbox.Submit(fixture.handle, start)); fixture.Poll(100);
  assert(!fixture.output.lastVoiceAccepted && fixture.voice.started == 0);
  fixture.Submit(LocalControlMessageKind::VoiceStart);
  assert(fixture.output.lastVoiceAccepted && fixture.voice.started == 1);
  assert(fixture.voice.lastRevision == fixture.control.Revision());
  // Losing a pure observer does not cancel the autonomous voice session.
  fixture.inbox.Disconnect(fixture.handle); fixture.Poll(100);
  assert(fixture.voice.stopped == 0);
  fixture.handle = {8, 2}; fixture.Connect();
  fixture.Submit(LocalControlMessageKind::Acquire);
  assert(fixture.voice.stopped == 1);
  fixture.Submit(LocalControlMessageKind::VoiceStart);
  assert(!fixture.output.lastVoiceAccepted && fixture.voice.started == 1);
  fixture.Submit(LocalControlMessageKind::VoiceStop);
  assert(fixture.voice.stopped == 2);
  fixture.Submit(LocalControlMessageKind::Stop);
  assert(fixture.voice.stopped == 3);
  fixture.Submit(LocalControlMessageKind::VoiceStart);
  assert(!fixture.output.lastVoiceAccepted && fixture.voice.started == 1);
}

void TestMaintenanceReservation() {
  for (const auto kind : {LocalControlMessageKind::Acquire,
                         LocalControlMessageKind::VoiceStart,
                         LocalControlMessageKind::Text,
                         LocalControlMessageKind::Selection}) {
    Fixture fixture;
    fixture.Connect();
    fixture.owner.SetMaintenance(true);
    fixture.Submit(kind);
    assert(!fixture.control.HasManualControl());
    assert(!fixture.conversation.HasActiveTurn());
    assert(fixture.voice.started == 0 && fixture.port.vibrations == 0);
  }
  Fixture observer;
  observer.Connect();
  observer.owner.SetMaintenance(true);
  observer.Submit(LocalControlMessageKind::StateGet);
  observer.Submit(LocalControlMessageKind::VoiceStop);
  observer.Submit(LocalControlMessageKind::Stop);
  assert(observer.voice.stopped == 2);
}

void TestBusySelectionKeepsObserverAvailable() {
  for (const bool maintenance : {false, true}) {
    Fixture fixture;
    fixture.Connect();
    fixture.Submit(LocalControlMessageKind::Acquire);
    if (maintenance) {
      fixture.owner.SetMaintenance(true);
    } else {
      fixture.dance.status.phase = "playing";
    }
    fixture.Submit(LocalControlMessageKind::Selection);
    assert(fixture.output.lastCommand.error == CommandError::Busy);
    assert(fixture.transport.closes == 0);
    assert(fixture.control.HasManualControl());
    assert(fixture.journal.begins == 0 && fixture.port.vibrations == 0);
    const int snapshots = fixture.output.snapshots;
    fixture.Submit(LocalControlMessageKind::StateGet, 101);
    assert(fixture.output.snapshots > snapshots);
    fixture.Submit(LocalControlMessageKind::Heartbeat, 102);
    assert(fixture.output.heartbeats == 1);
    fixture.Submit(LocalControlMessageKind::Stop, 103);
    assert(fixture.control.NeedsRecovery());
    assert(fixture.transport.closes == 0);
  }
}

void TestControlRefusalRetryAndLegacyCompatibility() {
  for (const bool acknowledge : {false, true}) {
    Fixture fixture;
    fixture.Connect();
    auto acquire = fixture.Message(LocalControlMessageKind::Acquire);
    acquire.acknowledge = acknowledge;
    fixture.owner.SetMaintenance(true);
    assert(fixture.inbox.Submit(fixture.handle, acquire));
    fixture.Poll(100);
    assert(fixture.output.controlAcks == (acknowledge ? 1 : 0));
    if (acknowledge) assert(fixture.output.lastControlError == LocalControlError::Busy);
    assert(!fixture.control.HasManualControl() && fixture.transport.closes == 0);
    fixture.Submit(LocalControlMessageKind::StateGet, 101);
    fixture.owner.SetMaintenance(false);
    assert(fixture.inbox.Submit(fixture.handle, acquire));
    fixture.Poll(102);
    assert(fixture.control.HasManualControl() && fixture.transport.closes == 0);
    assert(fixture.output.lastControlError == LocalControlError::None);
    assert(fixture.inbox.Submit(fixture.handle, acquire));
    fixture.Poll(103);
    if (acknowledge) assert(fixture.output.lastControlError == LocalControlError::ControllerBusy);
    assert(fixture.control.HasManualControl() && fixture.transport.closes == 0);
    fixture.Submit(LocalControlMessageKind::Stop, 104);
    auto reset = fixture.Message(LocalControlMessageKind::Reset);
    reset.acknowledge = acknowledge;
    assert(fixture.inbox.Submit(fixture.handle, reset));
    fixture.Poll(105);
    if (acknowledge) assert(fixture.output.lastControlError == LocalControlError::RecoveryRequired);
    assert(fixture.control.NeedsRecovery() && fixture.transport.closes == 0);
    fixture.recovery.allow = true;
    assert(fixture.inbox.Submit(fixture.handle, reset));
    fixture.Poll(106);
    assert(!fixture.control.NeedsRecovery() && fixture.control.HasManualControl());
    assert(fixture.output.lastControlError == LocalControlError::None);
    auto release = fixture.Message(LocalControlMessageKind::Release);
    release.acknowledge = acknowledge;
    assert(fixture.inbox.Submit(fixture.handle, release));
    fixture.Poll(107);
    assert(!fixture.control.HasManualControl() && fixture.transport.closes == 0);
    assert(fixture.inbox.Submit(fixture.handle, acquire));
    fixture.Poll(108);
    if (acknowledge) {
      assert(fixture.output.lastControlKind == LocalControlMessageKind::Acquire);
      assert(fixture.output.lastControlError == LocalControlError::RecoveryRequired);
    }
    assert(fixture.control.NeedsRecovery() && !fixture.control.HasManualControl());
  }
}

void TestRefusalsDoNotRenewLease() {
  Fixture fixture;
  fixture.Connect();
  fixture.Submit(LocalControlMessageKind::Acquire);
  fixture.owner.SetMaintenance(true);
  auto blocked = fixture.Message(LocalControlMessageKind::Acquire);
  blocked.acknowledge = true;
  assert(fixture.inbox.Submit(fixture.handle, blocked));
  fixture.Poll(5000);
  assert(fixture.control.HasManualControl() && fixture.transport.closes == 0);
  fixture.Submit(LocalControlMessageKind::Heartbeat, 6100);
  assert(!fixture.control.HasManualControl() && fixture.transport.closes == 1);
  assert(fixture.output.heartbeats == 0);

  Fixture outputFailure;
  outputFailure.Connect();
  outputFailure.owner.SetMaintenance(true);
  outputFailure.output.accept = false;
  assert(outputFailure.inbox.Submit(outputFailure.handle, blocked));
  outputFailure.Poll(100);
  assert(outputFailure.transport.closes == 1 && !outputFailure.control.HasManualControl());

  for (const auto kind : {LocalControlMessageKind::Reset, LocalControlMessageKind::Release}) {
    Fixture wrongLease;
    wrongLease.Connect();
    wrongLease.Submit(LocalControlMessageKind::Acquire);
    wrongLease.owner.SetMaintenance(true);
    auto invalid = wrongLease.Message(kind);
    invalid.controlLeaseId = Turn;
    invalid.acknowledge = true;
    assert(wrongLease.inbox.Submit(wrongLease.handle, invalid));
    wrongLease.Poll(101);
    assert(wrongLease.transport.closes == 1 && !wrongLease.control.HasManualControl());
    assert(wrongLease.output.controlAcks == 0);
  }
}

void TestStaleCommandThenStopAndUnsupportedCapability() {
  Fixture stale;
  stale.Connect();
  stale.Submit(LocalControlMessageKind::Acquire);
  auto command = stale.Message(LocalControlMessageKind::Selection);
  ++command.expectedRevision;
  assert(stale.inbox.Submit(stale.handle, command));
  stale.Poll(100);
  assert(stale.output.lastCommand.error == CommandError::StaleRevision);
  assert(stale.transport.closes == 0 && stale.journal.begins == 0);
  stale.Submit(LocalControlMessageKind::StateGet, 101);
  command = stale.Message(LocalControlMessageKind::Selection);
  command.commandId = Turn;
  command.sequence = 2;
  assert(stale.inbox.Submit(stale.handle, command));
  stale.Poll(102);
  assert(stale.output.lastCommand.error == CommandError::None && stale.journal.begins == 1);
  stale.Submit(LocalControlMessageKind::Stop, 103);
  assert(stale.control.NeedsRecovery() && stale.port.vibrations == 0);

  Fixture unsupported(false, false);
  unsupported.Connect();
  unsupported.Submit(LocalControlMessageKind::DanceStart);
  assert(!unsupported.output.lastDanceAccepted && unsupported.transport.closes == 0);
  unsupported.Submit(LocalControlMessageKind::StateGet, 101);
  unsupported.Submit(LocalControlMessageKind::VoiceStart, 101);
  assert(!unsupported.output.lastVoiceAccepted && unsupported.voice.started == 0);
  unsupported.Submit(LocalControlMessageKind::Stop, 102);
  assert(unsupported.dance.starts == 0 && unsupported.transport.closes == 0);
}

}  // namespace

void TestStaleEpochRefusalLeavesStatusAndStopAvailable() {
  Fixture fixture;
  fixture.Connect();
  fixture.Submit(LocalControlMessageKind::Acquire);
  auto request = fixture.Message(LocalControlMessageKind::Selection);
  request.hasOperationEpoch = true;
  request.operationEpoch = 1;
  assert(fixture.inbox.Submit(fixture.handle, request));
  fixture.Poll(100);
  assert(fixture.output.lastCommand.error == CommandError::WrongOperationEpoch);
  assert(fixture.journal.begins == 0 && !fixture.control.HasPendingSelection());
  assert(fixture.transport.closes == 0);
  const int before = fixture.output.snapshots;
  request = fixture.Message(LocalControlMessageKind::StateGet);
  request.includeOperationEpoch = true;
  assert(fixture.inbox.Submit(fixture.handle, request));
  fixture.Poll(100);
  assert(fixture.output.scopedEpoch && fixture.output.snapshots > before);
  fixture.Submit(LocalControlMessageKind::Stop);
  assert(fixture.transport.closes == 0 && fixture.safety.NeedsRecovery());
}

void TestEpochNegotiationResetsWithConnection() {
  Fixture fixture;
  fixture.Connect();
  fixture.Submit(LocalControlMessageKind::StateGet);
  assert(!fixture.output.scopedEpoch);
  auto request = fixture.Message(LocalControlMessageKind::StateGet);
  request.includeOperationEpoch = true;
  assert(fixture.inbox.Submit(fixture.handle, request));
  fixture.Poll(100);
  assert(fixture.output.scopedEpoch);
  fixture.Submit(LocalControlMessageKind::StateGet);
  assert(fixture.output.scopedEpoch);
  fixture.inbox.Disconnect(fixture.handle);
  fixture.Poll(100);
  fixture.Connect();
  fixture.Submit(LocalControlMessageKind::StateGet);
  assert(!fixture.output.scopedEpoch);
}

int main() {
  TestDeferredManualAckAndCoalescing();
  TestDeferredManualAckFailureAndUrgentStop();
  TestStopWhileActualJournalWriteIsStalled();
  TestDeferredManualLifetimeAndOriginalEpoch();
  TestDeferredReloadHasNoEarlyAcknowledgement();
  TestPhysicalCloseWaitsForDurableAck();
  TestStopDuringPhysicalCloseAndInspectedRecovery();
  TestLateBeginRecoveryAndLaterStop();
  TestManualLostCommitAckFailsClosed();
  TestStaleEpochRefusalLeavesStatusAndStopAvailable();
  TestEpochNegotiationResetsWithConnection();
  {
    Fixture fixture;
    fixture.Connect();
    fixture.Submit(LocalControlMessageKind::DanceStart);
    assert(!fixture.output.lastDanceAccepted && fixture.dance.starts == 0);
    fixture.owner.SetDanceAdmission(true);
    auto stale = fixture.Message(LocalControlMessageKind::DanceStart);
    ++stale.expectedRevision;
    fixture.inbox.Submit(fixture.handle, stale); fixture.Poll(100);
    assert(!fixture.output.lastDanceAccepted && fixture.dance.starts == 0);
    fixture.voice.status.phase = "listening";
    fixture.Submit(LocalControlMessageKind::DanceStart);
    assert(!fixture.output.lastDanceAccepted && fixture.dance.starts == 0);
    fixture.voice.status.phase = "idle";
    fixture.Submit(LocalControlMessageKind::DanceStart);
    assert(fixture.output.lastDanceAccepted && fixture.dance.starts == 1);
    ConversationRuntimeSnapshot snapshot;
    assert(!fixture.state.Read(snapshot));
    fixture.Submit(LocalControlMessageKind::DanceStart);
    assert(!fixture.output.lastDanceAccepted && fixture.dance.starts == 1);
    fixture.inbox.Disconnect(fixture.handle); fixture.Poll(100);
    assert(fixture.dance.stops == 0); // Observer disconnect preserves dance.
    fixture.handle = {8, 2}; fixture.Connect();
    fixture.Submit(LocalControlMessageKind::Stop);
    assert(fixture.dance.stops == 1);
  }
  {
    Fixture fixture;
    fixture.Connect(); fixture.owner.SetDanceAdmission(true);
    fixture.Submit(LocalControlMessageKind::Acquire);
    fixture.Submit(LocalControlMessageKind::DanceStart);
    assert(!fixture.output.lastDanceAccepted && fixture.dance.starts == 0);
  }
  TestMaintenanceReservation();
  TestBusySelectionKeepsObserverAvailable();
  TestControlRefusalRetryAndLegacyCompatibility();
  TestRefusalsDoNotRenewLease();
  TestStaleCommandThenStopAndUnsupportedCapability();
  TestStandaloneVoice();
  TestHelloAndObserver();
  TestLegacyTextRequiresUpgrade();
  TestExpiryCancelFailureAndReset();
  TestStopBetweenAckAndBegin();
  TestRetiredTextHasNoReply();
}

#include <algorithm>
#include <cassert>
#include <cstdio>
#include <functional>
#include <iostream>
#include <memory>
#include <vector>

#include "physical_effects_test_support.h"
#include "../src/features/device_control/DeviceStorageRuntime.h"
#include "../src/features/device_control/DeviceStorageStartup.h"
#include "../src/features/offline_show/OfflineEvidenceBridge.h"
#include "../src/features/local_control/LocalControlOwner.h"
#include "../src/features/local_control/LocalControlTransport.h"
#include "../src/features/local_control/LocalConversationNetworkPort.h"
#include "../src/features/show_cues/DeviceShowRuntime.h"

namespace {
using namespace kufr::offline;
const char* const Device = "offline-local-device";
const char* const Boot = "a85743d5-1b72-41b8-8500-ae3c102b27d6";
const char* const Lease = "b8287a95-b6a1-4501-bdaa-38721ec83191";
const char* const Evening = "cc655612-864c-4482-bb68-7415921119ae";
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
    assert(workerIo); length = bytes.size();
    if (length > capacity) return JournalReadResult::Failed;
    if (!length) return JournalReadResult::Missing;
    std::copy(bytes.begin(), bytes.end(), output); return JournalReadResult::Found;
  }
  bool Write(const uint8_t* input, size_t length) override {
    assert(workerIo); ++writes;
    if (duringWrite) duringWrite();
    bytes.assign(input, input + length); return true;
  }
  std::vector<uint8_t> bytes;
  std::function<void()> duringWrite;
  unsigned writes = 0;
};
JournalIdentityView Identity(bool provisioned = true) {
  JournalIdentityView identity;
  identity.healthy = true; identity.provisioned = provisioned;
  if (provisioned) identity.deviceId = Device;
  return identity;
}
struct DurableBytes {
  std::vector<uint8_t> journal, marker, online, offline;
};
struct BoundStorage {
  explicit BoundStorage(unsigned boot = 0, const DurableBytes* persisted = nullptr)
      : bootId(boot ? Id(100000 + boot) : Boot) {
    IoScope io;
    if (persisted) {
      journal.bytes = persisted->journal; marker.bytes = persisted->marker;
      online.bytes = persisted->online; offline.bytes = persisted->offline;
    } else {
      PersistentCommandJournal core(journal);
      JournalIdentityOwner identity(marker, core);
      KeepsakeCommittedInventory onlineSource(online);
      OfflineCommittedInventory offlineSource(offline);
      assert(identity.BeginFactory(Device, Identity(false), true));
      assert(core.EstablishScopedIdentity(Device, true));
      assert(onlineSource.Establish(Device, true));
      assert(offlineSource.EstablishBinding(Device, true));
      JournalEvidenceInventory a, b;
      assert(onlineSource.ReadCommitted(a) && offlineSource.ReadCommitted(b));
      assert(identity.Finalize(Identity(), a, b, true));
      assert(core.PersistReadyMask(63));
    }
    startup = std::make_unique<DeviceStorageStartup>(journal, marker, online, offline, Identity(),
        DeviceStorageRuntime::Policy::BoundedCompaction);
    assert(startup->Boot() && startup->CurrentState() == DeviceStorageStartup::State::Established);
    assert(startup->ImageStorageHealthy() && !startup->RequiresService());
    storage = startup->Runtime();
    assert(storage && startup->ControlJournal().CurrentEpoch() != 0);
  }
  bool Work() { IoScope io; return startup->RunOne(); }
  DurableBytes CaptureBytes() const { return {journal.bytes, marker.bytes, online.bytes, offline.bytes}; }
  std::string bootId;
  Memory journal, marker, online, offline;
  std::unique_ptr<DeviceStorageStartup> startup;
  DeviceStorageRuntime* storage = nullptr;
};
const uint8_t Samples[] = {0, 0};
const Clip Clips[] = {
  {"intro-1", "test", Samples, 2, 20, "hash", 0}, {"intro-2", "test", Samples, 2, 20, "hash", 0},
  {"ending-1", "test", Samples, 2, 20, "hash", 0}, {"ending-2", "test", Samples, 2, 20, "hash", 0},
  {"slot-1", "test", Samples, 2, 20, "hash", 1}, {"slot-2", "test", Samples, 2, 20, "hash", 2},
  {"slot-3", "test", Samples, 2, 20, "hash", 3}, {"slot-4", "test", Samples, 2, 20, "hash", 4},
  {"slot-5", "test", Samples, 2, 20, "hash", 5}, {"slot-6", "test", Samples, 2, 20, "hash", 6},
};
const BankView Bank{Clips, sizeof(Clips) / sizeof(Clip), 20, 100000, true,
    "test", "ollivander-private-party-v1", "marin"};
class Clock final : public LocalControlClockPort {
 public:
  uint64_t NowMs() const override { return now; }
  uint64_t now = 30;
};
class Audio final : public AudioPort {
 public:
  explicit Audio(const Clock& clock) : clock(clock) {}
  bool Available() const override { return true; }
  bool Play(const Clip&, uint64_t) override {
    if (Busy()) return false;
    until = clock.now + 20; ++plays; return true;
  }
  void Cancel() override { until = clock.now; }
  bool Busy() const override { return clock.now < until; }
  bool Failed() const override { return false; }
  const Clock& clock;
  uint64_t until = 0;
  unsigned plays = 0;
};
class Network final : public LocalConversationNetworkPort {
 public:
  bool QueueTextTurn(const std::string&, const std::string&, const std::string&,
      const ConversationRuntimeSnapshot&) override { return false; }
  bool QueueCancelFor(const std::string&, uint64_t) override { return true; }
  bool TakeFailedTurnFor(const std::string&, const std::string&, uint64_t) override { return false; }
};
class Transport final : public LocalControlTransport {
 public:
  explicit Transport(LocalControlInbox& inbox) : inbox(inbox) {}
  bool Send(const LocalSocketAdmission::Handle&, const std::string&) override { return true; }
  void Close(const LocalSocketAdmission::Handle& handle) override { ++closes; inbox.Disconnect(handle); }
  LocalControlInbox& inbox;
  unsigned closes = 0;
};
class Output final : public LocalControlOutputPort {
 public:
  struct Ack {
    LocalSocketAdmission::Handle handle;
    LocalControlMessage message;
    bool accepted;
    std::string error, records;
  };
  struct ControlReceipt { LocalSocketAdmission::Handle handle; LocalControlMessage message; LocalControlError error; };
  bool ControlAck(const LocalSocketAdmission::Handle& handle, const LocalControlMessage& message, LocalControlError error) override {
    lastControlError = error; controls.push_back({handle, message, error}); return true;
  }
  bool OfflineAck(const LocalSocketAdmission::Handle& handle, const LocalControlMessage& message,
      bool accepted, const std::string& error, const std::string&, const std::string& records) override {
    acks.push_back({handle, message, accepted, error, records}); return true;
  }
  bool Welcome(const LocalSocketAdmission::Handle&, const std::string&, uint64_t) override { ++welcomes; return true; }
  bool Snapshot(const LocalSocketAdmission::Handle&, const std::string& lease, uint64_t) override {
    lastLease = lease; return true;
  }
  bool Heartbeat(const LocalSocketAdmission::Handle&, const LocalControlMessage&, uint64_t) override { return true; }
  struct CommandDelivery {
    LocalSocketAdmission::Handle handle;
    LocalControlMessage message;
    CommandReceipt receipt;
    bool sent;
  };
  bool CommandAck(const LocalSocketAdmission::Handle& handle, const LocalControlMessage& message,
      const CommandReceipt& receipt) override {
    if (beforeCommandAck) beforeCommandAck();
    commands.push_back({handle, message, receipt, commandAckSucceeds});
    return commandAckSucceeds;
  }
  size_t CommandCount(const LocalControlMessage& message) const {
    size_t count = 0;
    for (const auto& delivery : commands)
      count += delivery.message.messageId == message.messageId && delivery.sent;
    return count;
  }
  std::vector<CommandDelivery> commands;
  std::function<void()> beforeCommandAck;
  bool commandAckSucceeds = true;
  bool ConversationAck(const LocalSocketAdmission::Handle&, const LocalControlMessage&,
      const ConversationCommandReceipt&) override { return true; }
  bool ConversationResult(const LocalSocketAdmission::Handle&, const std::string&, uint64_t, const std::string&) override { return true; }
  size_t Count(const LocalControlMessage& message, bool accepted) const {
    size_t count = 0;
    for (const auto& ack : acks) count += ack.message.kind == message.kind
        && ack.message.messageId == message.messageId && ack.accepted == accepted;
    return count;
  }
  std::vector<Ack> acks;
  std::vector<ControlReceipt> controls;
  size_t ControlCount(const LocalControlMessage& request, LocalControlError error) const {
    size_t count = 0;
    for (const auto& receipt : controls)
      count += receipt.message.kind == request.kind && receipt.message.messageId == request.messageId && receipt.error == error;
    return count;
  }
  std::string lastLease;
  LocalControlError lastControlError = LocalControlError::None;
  unsigned welcomes = 0;
};
class Ids final : public LocalControlIdPort {
 public:
  std::string NewLeaseId() override { return Lease; }
};
class Recovery final : public LocalControlRecoveryGate {
 public:
  bool InspectionAllowsRecovery() const override { ++inspections; return allow; }
  mutable unsigned inspections = 0;
  bool allow = true;
};
class Steps final : public LocalControlStepPort {
 public:
  void OnStep(const SelectionStep&) override {}
};
struct Fixture : BoundStorage {
  explicit Fixture(unsigned boot = 0, const DurableBytes* persisted = nullptr)
      : BoundStorage(boot, persisted), effects(io), safetyPort(effects), safety(safetyPort), sequence(safety, true),
        control(Device, bootId, sequence, startup->ControlJournal(), 1), state(false, 0, ""),
        runtime(control, sequence, state, OfflineShow::CatalogVersion()), physical(effects, runtime),
        presentation(physical), audio(clock), bridge(storage->EvidenceRequests(), *storage),
        show(runtime, state, control, presentation, physical, audio, bridge, Device, bootId, Evening, &Bank),
        conversation(bootId, Id(900)), inbox(Device, bootId), transport(inbox),
        owner(Device, bootId, runtime, state, conversation, network, inbox, transport, output, ids, recovery, steps, clock) {
    calibration.fill({1100, 1900, 120});
    assert(effects.Configure(calibration, 1)); effects.Tick(0); effects.Tick(clock.now);
    assert(effects.RecoverAfterInspection() && runtime.BindPresentation(presentation));
    physical.BindClock(ReadClock, this); physical.BindBootIdentity(bootId);
    for (unsigned attempt = 0; attempt != 12 && !runtime.RecoverAfterInspection(); ++attempt) Work();
    assert(!runtime.NeedsRecovery());
    owner.BindOffline(show);
    assert(std::string(show.Preflight()) == "ready");
  }
  static uint64_t ReadClock(void* context) { return static_cast<Fixture*>(context)->clock.now; }
  LocalControlMessage Message(LocalControlMessageKind kind, unsigned id) const {
    LocalControlMessage message;
    message.kind = kind; message.messageId = Id(id); message.commandId = message.messageId;
    message.deviceId = Device; message.bootId = bootId; message.controlLeaseId = Lease;
    message.generation = runtime.Generation(); message.expectedRevision = control.Revision();
    message.operationEpoch = control.OperationEpoch(); message.hasOperationEpoch = true;
    message.compartment = 3; message.issuedAtDeviceMs = clock.now; message.maxAgeMs = 1000;
    message.acknowledge = true; message.includeOperationEpoch = true;
    return message;
  }
  void Send(const LocalControlMessage& message) { assert(inbox.Submit(handle, message)); owner.Poll(); }
  void Connect(LocalSocketAdmission::Handle next = {7, 1}) {
    handle = next; assert(inbox.Open(handle, clock.now)); owner.Poll();
    Send(Message(LocalControlMessageKind::Hello, 1));
  }
  void Acquire() {
    Send(Message(LocalControlMessageKind::Acquire, 2));
    assert(output.lastControlError == LocalControlError::None && output.lastLease == Lease && control.HasManualControl());
  }
  void Tick(bool worker = true, DeviceShowRuntime* serverShow = nullptr) {
    clock.now += 10;
    presentation.CheckDeadline(clock.now); effects.Tick(clock.now);
    owner.Tick(); // This is the only runtime Tick, as in production.
    presentation.CheckDeadline(clock.now); physical.Tick(clock.now);
    if (serverShow) serverShow->Tick(clock.now, true);
    show.Tick(clock.now, true);
    if (worker) Work();
  }
  void Await(const LocalControlMessage& message, unsigned maximum = 40) {
    for (unsigned attempt = 0; attempt < maximum && !output.Count(message, true); ++attempt) Tick();
    assert(output.Count(message, true) == 1);
  }
  void Arm() {
    Connect(); Acquire();
    const auto arm = Message(LocalControlMessageKind::OfflineArm, 10);
    Send(arm); assert(!output.Count(arm, true) && show.LastOperationIsPending()); Await(arm);
    assert(show.Armed() && runtime.OfflineAuthority());
    for (unsigned tick = 0; tick < 3; ++tick) Tick();
  }
  Outbox Published() const { Outbox outbox; assert(storage->EvidenceRequests().TryReadOffline(outbox)); return outbox; }
  Outcome Find(const std::string& id) const {
    for (const auto& record : Published().records) if (id == record.operationId.data()) return record;
    return {};
  }
  FakeIo io;
  PhysicalEffects effects;
  SafetyAdapter safetyPort;
  SafetyController safety;
  SelectionSequence sequence;
  DeviceControl control;
  ConversationRuntimeState state;
  DeviceControlRuntime runtime;
  PhysicalPresentation physical;
  WandPresentation presentation;
  Clock clock;
  Audio audio;
  OfflineEvidenceBridge bridge;
  OfflineShow show;
  ConversationSession conversation;
  Network network;
  LocalControlInbox inbox;
  Transport transport;
  Output output;
  Ids ids;
  Recovery recovery;
  Steps steps;
  LocalControlOwner owner;
  std::array<ServoCalibration, 6> calibration{};
  LocalSocketAdmission::Handle handle{7, 1};
};

void ManualOwnerLifecyclesAcrossProductionBoots() {
  constexpr unsigned Boots = 128, PerBoot = 8;
  static_assert(Boots * PerBoot == 1024, "Keep the actual owner workload explicit");
  DurableBytes persisted;
  LocalControlMessage first;
  uint64_t compactions = 0, simulated = 0, finalEpoch = 0;
  unsigned delivered = 0, heartbeats = 0;
  for (unsigned boot = 0; boot != Boots; ++boot) {
    // Every prior owner and its reference-holding consumers are destroyed before
    // the next startup reconstructs authority exclusively from durable bytes.
    Fixture fixture(boot + 1, boot ? &persisted : nullptr);
    fixture.Connect(); fixture.Acquire();
    for (unsigned cycle = 0; cycle != PerBoot; ++cycle) {
      const unsigned operation = boot * PerBoot + cycle + 1;
      fixture.Send(fixture.Message(LocalControlMessageKind::Heartbeat, 20000 + operation));
      ++heartbeats;
      auto command = fixture.Message(LocalControlMessageKind::Selection, 1000 + operation);
      command.sequence = cycle + 1;
      if (operation == 1) first = command;
      const auto epoch = fixture.control.OperationEpoch();
      const auto revision = fixture.control.Revision();
      const int servoBefore = fixture.io.servoWrites, vibrationBefore = fixture.io.vibrationWrites;
      fixture.output.beforeCommandAck = [&]() {
        assert(!fixture.sequence.IsBusy());
        assert(fixture.io.servoWrites == servoBefore && fixture.io.vibrationWrites == vibrationBefore);
        assert(fixture.control.HasManualControl() && fixture.clock.now < command.issuedAtDeviceMs + command.maxAgeMs);
      };
      fixture.Send(command);
      fixture.Send(command); // Pending exact duplicate must retain the original envelope.
      assert(fixture.output.CommandCount(command) == 0 && fixture.output.commands.size() == cycle);
      assert(!fixture.sequence.IsBusy() && fixture.io.servoWrites == servoBefore);
      fixture.Tick(false);
      assert(fixture.output.CommandCount(command) == 0 && !fixture.sequence.IsBusy());
      assert(fixture.Work()); // Journal begin is durable before owner ACK adoption.
      assert(fixture.output.CommandCount(command) == 0 && !fixture.sequence.IsBusy());
      fixture.owner.Tick();
      assert(fixture.output.CommandCount(command) == 1 && fixture.sequence.IsBusy());
      const auto& ack = fixture.output.commands.back();
      assert(ack.sent && ack.receipt.error == CommandError::None && ack.receipt.outcome == CommandOutcome::Accepted);
      assert(!ack.receipt.deferred && !ack.receipt.duplicate);
      assert(ack.handle.generation == fixture.handle.generation && ack.handle.socket == fixture.handle.socket);
      assert(ack.message.bootId == command.bootId && ack.message.generation == command.generation);
      assert(ack.message.operationEpoch == epoch && ack.message.sequence == command.sequence);
      assert(ack.message.expectedRevision == revision && ack.message.issuedAtDeviceMs == command.issuedAtDeviceMs);
      assert(ack.message.maxAgeMs == command.maxAgeMs && ack.message.controlLeaseId == command.controlLeaseId);
      fixture.output.beforeCommandAck = {};
      ++delivered;
      const auto started = fixture.clock.now;
      while (fixture.control.HasPendingSelection()) {
        assert(fixture.clock.now - started < 15000);
        if ((fixture.clock.now - started) % 1000 == 0) {
          fixture.Send(fixture.Message(LocalControlMessageKind::Heartbeat, 30000 + operation));
          ++heartbeats;
        }
        fixture.Tick();
      }
      assert(fixture.control.LastResult().commandId == command.commandId);
      assert(fixture.control.LastResult().outcome == CommandOutcome::Completed);
      assert(fixture.io.servoWrites > servoBefore && fixture.io.vibrationWrites > vibrationBefore);
      assert(!fixture.sequence.IsBusy() && !fixture.storage->ControlJournal().HasPending());
      assert(fixture.output.CommandCount(command) == 1 && fixture.transport.closes == 0);
      for (unsigned drain = 0; drain != 3; ++drain) fixture.Tick();
      finalEpoch = fixture.control.OperationEpoch();
    }
    compactions += fixture.storage->Compactions(); simulated += fixture.clock.now - 30;
    persisted = fixture.CaptureBytes();
  }
  assert(delivered == Boots * PerBoot && compactions >= 2 && finalEpoch > first.operationEpoch);
  Fixture reopened(Boots + 1, &persisted); reopened.Connect(); reopened.Acquire();
  const auto servoBefore = reopened.io.servoWrites;
  auto replay = first; replay.bootId = reopened.bootId;
  reopened.Send(replay);
  assert(reopened.output.CommandCount(replay) == 1);
  assert(reopened.output.commands.back().receipt.error == CommandError::WrongOperationEpoch);
  assert(reopened.io.servoWrites == servoBefore && !reopened.sequence.IsBusy());
  std::printf("manual LocalControlOwner production-startup workload: cycles=%u boots=%u verificationBoots=1 compactions=%llu finalEpoch=%llu simulatedMs=%llu successfulACKs=%u heartbeats=%u\n",
      Boots * PerBoot, Boots, static_cast<unsigned long long>(compactions), static_cast<unsigned long long>(finalEpoch),
      static_cast<unsigned long long>(simulated), delivered, heartbeats);
}

void ManualOwnerFailedAckAndExpiredDeferredIntentNeverMove() {
  for (bool expires : {false, true}) {
    Fixture fixture; fixture.Connect(); fixture.Acquire();
    auto command = fixture.Message(LocalControlMessageKind::Selection, 9000);
    command.sequence = 1;
    const auto servoBefore = fixture.io.servoWrites, vibrationBefore = fixture.io.vibrationWrites;
    fixture.Send(command);
    assert(fixture.output.commands.empty() && !fixture.sequence.IsBusy());
    assert(fixture.Work());
    if (expires) fixture.clock.now += command.maxAgeMs + 1;
    else fixture.output.commandAckSucceeds = false;
    fixture.owner.Tick();
    assert(fixture.output.commands.size() == 1);
    const auto& ack = fixture.output.commands.back();
    if (expires) assert(ack.sent && ack.receipt.error == CommandError::Expired);
    else assert(!ack.sent && ack.receipt.error == CommandError::None && fixture.transport.closes != 0);
    for (unsigned drain = 0; drain != 20; ++drain) fixture.Tick();
    assert(fixture.io.servoWrites == servoBefore && fixture.io.vibrationWrites == vibrationBefore);
    assert(!fixture.sequence.IsBusy() && fixture.control.NeedsRecovery());
  }
}

void ManualOwnerFinalAckCannotReviveRevokedIntent() {
  enum class Cut { Stop, SocketRevocation, AckTtlExpiry, AckLeaseExpiry };
  for (const auto cut : {Cut::Stop, Cut::SocketRevocation, Cut::AckTtlExpiry, Cut::AckLeaseExpiry}) {
    Fixture fixture; fixture.Connect(); fixture.Acquire();
    auto command = fixture.Message(LocalControlMessageKind::Selection, 9100);
    command.sequence = 1;
    const auto servoBefore = fixture.io.servoWrites, vibrationBefore = fixture.io.vibrationWrites;
    fixture.Send(command);
    assert(fixture.output.commands.empty() && !fixture.sequence.IsBusy());
    if (cut != Cut::Stop) {
      assert(fixture.Work());
      fixture.output.beforeCommandAck = [&]() {
        if (cut == Cut::SocketRevocation) fixture.inbox.Disconnect(fixture.handle);
        else fixture.clock.now += cut == Cut::AckTtlExpiry
            ? command.maxAgeMs + 1 : DeviceControl::ManualLeaseDurationMs + 1;
      };
      fixture.owner.Tick();
      fixture.output.beforeCommandAck = {};
      assert(fixture.output.commands.size() == 1 && fixture.output.commands.back().sent);
      fixture.owner.Poll();
    } else {
      fixture.Send(fixture.Message(LocalControlMessageKind::Stop, 9101));
      assert(fixture.Work()); // The cancelled begin may still become durable.
      assert(fixture.output.commands.empty());
    }
    for (unsigned drain = 0; drain != 20; ++drain) fixture.Tick();
    assert(fixture.io.servoWrites == servoBefore && fixture.io.vibrationWrites == vibrationBefore);
    assert(!fixture.sequence.IsBusy() && fixture.control.NeedsRecovery());
    if (cut == Cut::SocketRevocation || cut == Cut::AckLeaseExpiry) assert(!fixture.control.HasManualControl());
    if (cut == Cut::Stop) assert(fixture.output.commands.empty());
  }
}

void PendingArmRetainsPostHandoffEnvelopeAcrossQueries() {
  Fixture fixture; fixture.Connect(); fixture.Acquire();
  const auto arm = fixture.Message(LocalControlMessageKind::OfflineArm, 10);
  fixture.Send(arm);
  const uint64_t afterHandoff = fixture.runtime.Generation(), afterRevision = fixture.control.Revision();
  assert(afterHandoff != arm.generation && !fixture.control.HasManualControl());
  assert(!fixture.output.Count(arm, true) && !fixture.show.Armed());
  fixture.Tick(false); fixture.Tick(false);
  const auto query = fixture.Message(LocalControlMessageKind::OfflineOutbox, 11);
  fixture.Send(query); assert(fixture.output.Count(query, true) == 1);
  fixture.Send(arm); // Exact original duplicate coalesces despite generation handoff.
  assert(!fixture.output.Count(arm, true) && !fixture.output.Count(arm, false));
  auto changed = arm; ++changed.expectedRevision;
  fixture.Send(changed);
  assert(fixture.output.Count(arm, false) == 1 && fixture.output.acks.back().error == "busy_or_stale");
  assert(fixture.runtime.Generation() == afterHandoff && fixture.control.Revision() == afterRevision);
  assert(!fixture.Published().offlineEnabled);
  fixture.Await(arm);
  assert(fixture.show.Armed() && fixture.Published().offlineEnabled);
  assert(fixture.output.acks.back().handle.generation == fixture.handle.generation);
  assert(fixture.runtime.Generation() == afterHandoff);
}

void StartAndSyncAckWaitForRealSourceResults() {
  Fixture fixture; fixture.Arm();
  const auto start = fixture.Message(LocalControlMessageKind::OfflineStart, 20);
  const uint64_t startedAt = fixture.clock.now;
  const int servoBefore = fixture.io.servoWrites;
  const unsigned audioBefore = fixture.audio.plays;
  fixture.Send(start);
  assert(fixture.show.Active() && !fixture.output.Count(start, true));
  for (unsigned tick = 0; tick != 5; ++tick) fixture.Tick(false);
  assert(fixture.audio.plays == audioBefore && fixture.io.servoWrites == servoBefore);
  assert(!fixture.Find(start.messageId).operationId[0] && !fixture.output.Count(start, true));
  fixture.Work(); // Journal Reserve commits; source Reserve is still separate.
  for (unsigned tick = 0; tick != 8 && !fixture.storage->EvidenceRequests().Busy(); ++tick) fixture.Tick(false);
  assert(fixture.storage->EvidenceRequests().Busy());
  assert(!fixture.output.Count(start, true) && !fixture.Find(start.messageId).operationId[0]);
  assert(fixture.audio.plays == audioBefore && fixture.io.servoWrites == servoBefore);
  fixture.Work(); // Actual OfflineReserve source write.
  fixture.Await(start);
  const auto reserved = fixture.Find(start.messageId);
  assert(reserved.operationId[0] && !reserved.completed && reserved.startedAtMs == startedAt);
  assert(reserved.generation == start.generation && reserved.deviceRevision == start.expectedRevision
      && reserved.operationEpoch == start.operationEpoch);
  for (unsigned tick = 0; tick != 1800 && fixture.show.Active(); ++tick) fixture.Tick();
  assert(!fixture.show.Active() && fixture.presentation.State() == WandPresentationState::Idle);
  const auto completed = fixture.Find(start.messageId);
  assert(completed.completed && completed.startedAtMs == startedAt);
  assert(fixture.control.WasTimedPresentationCompleted(start.messageId, start.compartment,
      start.operationEpoch, completed.physicalFingerprint));
  auto ack = fixture.Message(LocalControlMessageKind::OfflineSyncAck, 21); ack.commandId = start.messageId;
  ack.operationEpoch = completed.operationEpoch;
  fixture.Send(ack);
  assert(!fixture.output.Count(ack, true));
  const auto query = fixture.Message(LocalControlMessageKind::OfflineOutbox, 22);
  fixture.Send(query); fixture.Send(ack);
  for (unsigned tick = 0; tick != 5; ++tick) fixture.Tick(false);
  assert(!fixture.output.Count(ack, true));
  const auto stillPending = fixture.Find(start.messageId);
  assert(stillPending.completedAtMs == completed.completedAtMs && stillPending.completed);
  fixture.Await(ack);
  assert(!fixture.Find(start.messageId).operationId[0]);
  assert(fixture.output.Count(ack, true) == 1 && fixture.output.Count(ack, false) == 0);
}

void StopBeforeArmAcknowledgementAdoptsLateFacts() {
  Fixture fixture; fixture.Connect(); fixture.Acquire();
  const auto arm = fixture.Message(LocalControlMessageKind::OfflineArm, 30);
  fixture.Send(arm); fixture.Tick(false);
  const auto stop = fixture.Message(LocalControlMessageKind::Stop, 31);
  bool injected = false;
  fixture.offline.duringWrite = [&] { injected = true; assert(fixture.inbox.Submit(fixture.handle, stop)); };
  fixture.Work(); fixture.offline.duringWrite = {};
  assert(injected && fixture.Published().offlineEnabled);
  fixture.Tick(false);
  assert(!fixture.show.Armed() && !fixture.output.Count(arm, true));
  for (unsigned tick = 0; tick != 30; ++tick) fixture.Tick();
  assert(!fixture.output.Count(arm, true) && !fixture.show.Armed() && !fixture.Published().offlineEnabled);
  assert(!fixture.show.HasPendingStorage());
}

void StopBeforeStartReserveAckCannotStartMotion() {
  Fixture fixture; fixture.Arm();
  const auto start = fixture.Message(LocalControlMessageKind::OfflineStart, 40);
  const int servoBefore = fixture.io.servoWrites; const unsigned audioBefore = fixture.audio.plays;
  fixture.Send(start);
  assert(!fixture.output.Count(start, true));
  fixture.Send(fixture.Message(LocalControlMessageKind::Stop, 41));
  for (unsigned tick = 0; tick != 35; ++tick) fixture.Tick();
  assert(!fixture.show.Active() && !fixture.show.Armed());
  assert(fixture.io.servoWrites == servoBefore && fixture.audio.plays == audioBefore);
  assert(!fixture.output.Count(start, true) && !fixture.Find(start.messageId).operationId[0]);
}

void DisconnectOnlyAbandonsOldSocketReply() {
  Fixture fixture; fixture.Connect(); fixture.Acquire();
  const auto arm = fixture.Message(LocalControlMessageKind::OfflineArm, 50);
  fixture.Send(arm); fixture.Tick(false);
  const auto oldHandle = fixture.handle;
  fixture.inbox.Disconnect(oldHandle); fixture.owner.Poll();
  fixture.Connect({7, 2});
  const auto query = fixture.Message(LocalControlMessageKind::OfflineOutbox, 51);
  fixture.Send(query);
  for (unsigned tick = 0; tick != 25; ++tick) fixture.Tick();
  assert(fixture.show.Armed() && fixture.runtime.OfflineAuthority());
  assert(!fixture.output.Count(arm, true) && !fixture.output.Count(arm, false));
  assert(fixture.output.Count(query, true) == 1);
  assert(!fixture.inbox.Submit(oldHandle, fixture.Message(LocalControlMessageKind::Stop, 52)));
  fixture.inbox.Disconnect(oldHandle); fixture.owner.Poll(); // Stale disconnect cannot tear down the new socket.
  const auto nextQuery = fixture.Message(LocalControlMessageKind::OfflineOutbox, 53);
  fixture.Send(nextQuery);
  assert(fixture.output.Count(nextQuery, true) == 1 && fixture.show.Armed());
  for (const auto& reply : fixture.output.acks) if (reply.message.messageId == query.messageId
      || reply.message.messageId == nextQuery.messageId) assert(reply.handle.generation == 2);
  assert(fixture.transport.closes == 0 && !fixture.control.HasManualControl());
}

void ServerMailboxRetainsLatestSettingsUntilDurablePublication() {
  Fixture fixture;
  DeviceShowMailbox mailbox;
  DeviceShowRuntime serverShow(fixture.runtime, fixture.state, fixture.physical,
      fixture.show, fixture.audio, mailbox, Boot);
  DeviceShowSettings first;
  first.revision = 2; first.stopRevision = 3; first.quiet = true; first.ambient = false;
  mailbox.Settings(first);
  fixture.Tick(false, &serverShow);
  const uint64_t stoppedGeneration = fixture.runtime.Generation();
  assert(serverShow.TakeRemoteStop());
  DeviceShowSettings pending;
  assert(mailbox.PeekSettings(pending) && pending.revision == 2 && pending.stopRevision == 3);
  assert(fixture.show.ServerStopRevision() == 0 && fixture.show.ServerSettingsRevision() == 0);
  assert(fixture.Published().serverStopRevision == 0 && fixture.Published().serverSettingsRevision == 0);
  auto latest = first; latest.revision = 4; latest.stopRevision = 1;
  mailbox.Settings(latest); // The independent newer Stop revision must survive.
  assert(mailbox.PeekSettings(pending) && pending.revision == 4 && pending.stopRevision == 3);
  assert(!mailbox.ConfirmSettings(first)); // An old result cannot consume the replacement request.
  for (unsigned tick = 0; tick != 5; ++tick) {
    fixture.Tick(false, &serverShow);
    assert(!serverShow.TakeRemoteStop() && fixture.runtime.Generation() == stoppedGeneration);
    assert(mailbox.PeekSettings(pending));
  }
  bool confirmed = false;
  for (unsigned tick = 0; tick != 80; ++tick) {
    fixture.Tick(true, &serverShow);
    assert(!serverShow.TakeRemoteStop() && fixture.runtime.Generation() == stoppedGeneration);
    if (mailbox.PeekSettings(pending)) continue;
    const auto source = fixture.Published();
    assert(source.serverStopRevision == 3 && source.serverSettingsRevision == 4);
    assert(fixture.show.ServerStopRevision() == 3 && fixture.show.ServerSettingsRevision() == 4);
    confirmed = true; break;
  }
  assert(confirmed);
  DeviceShowSettings applied; uint64_t generation = 0, revision = 0;
  mailbox.PollState(applied, generation, revision);
  assert(applied.revision == 4 && applied.stopRevision == 3 && applied.quiet && !applied.ambient);
}

void PreparePendingInspectedSelection(Fixture& fixture) {
  fixture.Connect(); fixture.Acquire();
  auto selection = fixture.Message(LocalControlMessageKind::Selection, 700);
  selection.sequence = 1;
  fixture.Send(selection);
  fixture.Work();
  fixture.owner.Tick();
  assert(fixture.storage->ControlJournal().HasPending());
  fixture.Send(fixture.Message(LocalControlMessageKind::Stop, 701));
  assert(fixture.control.NeedsRecovery() && fixture.control.HasManualControl());
  assert(fixture.effects.RecoverAfterInspection());
}

void ResetRetainsOneInspectionAndDefersItsAcknowledgement() {
  Fixture fixture;
  PreparePendingInspectedSelection(fixture);
  fixture.owner.SetMaintenance(true);
  fixture.owner.SetRecoveryAdmission(true);
  const auto reset = fixture.Message(LocalControlMessageKind::Reset, 710);
  const uint64_t revision = fixture.control.Revision();
  fixture.Send(reset);
  assert(fixture.recovery.inspections == 1 && fixture.runtime.HasRecoveryWork());
  for (unsigned tick = 0; tick != 4; ++tick) fixture.Tick(false);
  fixture.Send(reset);
  auto changed = reset; changed.messageId = Id(711);
  fixture.Send(changed);
  assert(fixture.output.ControlCount(changed, LocalControlError::Busy) == 1);
  fixture.Send(fixture.Message(LocalControlMessageKind::StateGet, 712));
  fixture.Send(fixture.Message(LocalControlMessageKind::OfflineOutbox, 713));
  const auto quiet = fixture.Message(LocalControlMessageKind::Quiet, 714);
  fixture.Send(quiet);
  assert(fixture.output.Count(quiet, false) == 1 && fixture.transport.closes == 0);
  assert(fixture.output.ControlCount(reset, LocalControlError::None) == 0 && fixture.recovery.inspections == 1);
  for (unsigned tick = 0; tick != 40 && !fixture.output.ControlCount(reset, LocalControlError::None); ++tick) fixture.Tick();
  assert(fixture.output.ControlCount(reset, LocalControlError::None) == 1);
  assert(fixture.recovery.inspections == 1 && !fixture.runtime.HasRecoveryWork());
  assert(!fixture.control.NeedsRecovery() && fixture.control.Revision() == revision + 1);
  assert((fixture.control.ReadyMask() & (1u << (reset.compartment - 1))) == 0);
  for (unsigned tick = 0; tick != 4; ++tick) fixture.Tick();
  assert(fixture.output.ControlCount(reset, LocalControlError::None) == 1);
}

void StopAndDisconnectCannotDeliverAnOldResetReceipt() {
  for (unsigned committed = 0; committed != 2; ++committed) {
    Fixture fixture;
    PreparePendingInspectedSelection(fixture);
    const auto reset = fixture.Message(LocalControlMessageKind::Reset, 720);
    fixture.Send(reset);
    if (committed) fixture.Work();
    fixture.Send(fixture.Message(LocalControlMessageKind::Stop, 721));
    for (unsigned tick = 0; tick != 12; ++tick) fixture.Tick();
    assert(fixture.output.ControlCount(reset, LocalControlError::None) == 0);
    assert(fixture.control.NeedsRecovery() && fixture.recovery.inspections == 1);
  }
  Fixture fixture;
  PreparePendingInspectedSelection(fixture);
  const auto reset = fixture.Message(LocalControlMessageKind::Reset, 730);
  fixture.Send(reset);
  fixture.inbox.Disconnect(fixture.handle); fixture.owner.Poll();
  fixture.Connect({7, 2});
  for (unsigned tick = 0; tick != 12; ++tick) fixture.Tick();
  assert(fixture.output.ControlCount(reset, LocalControlError::None) == 0);
  assert(fixture.control.NeedsRecovery());
}

void LeaseExpiryAndMaintenanceCannotRenewInspection() {
  Fixture fixture;
  PreparePendingInspectedSelection(fixture);
  const auto reset = fixture.Message(LocalControlMessageKind::Reset, 740);
  fixture.owner.SetMaintenance(true);
  fixture.Send(reset);
  assert(fixture.output.ControlCount(reset, LocalControlError::Busy) == 1 && fixture.recovery.inspections == 0);
  fixture.owner.SetRecoveryAdmission(true);
  fixture.Send(reset);
  assert(fixture.recovery.inspections == 1);
  fixture.clock.now += DeviceControl::ManualLeaseDurationMs + 1;
  fixture.owner.Tick();
  for (unsigned tick = 0; tick != 12; ++tick) fixture.Tick();
  assert(fixture.output.ControlCount(reset, LocalControlError::None) == 0 && fixture.transport.closes != 0);
  assert(fixture.control.NeedsRecovery() && fixture.recovery.inspections == 1);
}
}

int main() {
  ManualOwnerLifecyclesAcrossProductionBoots();
  ManualOwnerFailedAckAndExpiredDeferredIntentNeverMove();
  ManualOwnerFinalAckCannotReviveRevokedIntent();
  PendingArmRetainsPostHandoffEnvelopeAcrossQueries();
  StartAndSyncAckWaitForRealSourceResults();
  StopBeforeArmAcknowledgementAdoptsLateFacts();
  StopBeforeStartReserveAckCannotStartMotion();
  DisconnectOnlyAbandonsOldSocketReply();
  ServerMailboxRetainsLatestSettingsUntilDurablePublication();
  ResetRetainsOneInspectionAndDefersItsAcknowledgement();
  StopAndDisconnectCannotDeliverAnOldResetReceipt();
  LeaseExpiryAndMaintenanceCannotRenewInspection();
  std::cout << "offline local control owner checks passed\n";
}

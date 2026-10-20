#include <algorithm>
#include <cassert>
#include <cstdio>
#include <memory>
#include <vector>

#include "../src/features/device_control/DeviceStorageRuntime.h"
#include "../src/features/device_control/DeviceStorageStartup.h"
#include "../src/features/offline_show/OfflineEvidenceBridge.h"
#include "physical_effects_test_support.h"

namespace {
using namespace kufr::offline;
bool WorkerIo = true;
std::string Id(unsigned value) {
  char text[37];
  std::snprintf(text, sizeof(text), "aaaaaaaa-aaaa-4aaa-8aaa-%012x", value);
  return text;
}
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

class Memory final : public JournalStorage {
 public:
  JournalReadResult Read(uint8_t* output, size_t capacity, size_t& length) override {
    assert(WorkerIo);
    length = bytes.size();
    if (length > capacity) return JournalReadResult::Failed;
    if (bytes.empty()) return JournalReadResult::Missing;
    std::copy(bytes.begin(), bytes.end(), output);
    return JournalReadResult::Found;
  }
  bool Write(const uint8_t* input, size_t length) override {
    assert(WorkerIo);
    if (cutBefore) return false;
    bytes.assign(input, input + length);
    return !cutAfter;
  }
  std::vector<uint8_t> bytes;
  bool cutBefore = false, cutAfter = false;
};
JournalIdentityView Identity(bool provisioned = true) {
  JournalIdentityView value;
  value.healthy = true; value.provisioned = provisioned;
  if (provisioned) value.deviceId = "offline-device";
  return value;
}
struct Durable {
  Durable() {
    WorkerIo = true;
    PersistentCommandJournal core(journal);
    JournalIdentityOwner identity(marker, core);
    KeepsakeCommittedInventory onlineOwner(online);
    OfflineCommittedInventory offlineOwner(offline);
    assert(identity.BeginFactory("offline-device", Identity(false), true));
    assert(core.EstablishScopedIdentity("offline-device", true));
    assert(onlineOwner.Establish("offline-device", true));
    assert(offlineOwner.EstablishBinding("offline-device", true));
    JournalEvidenceInventory a, b;
    assert(onlineOwner.ReadCommitted(a) && offlineOwner.ReadCommitted(b));
    assert(identity.Finalize(Identity(), a, b, true));
    assert(core.PersistReadyMask(63));
  }
  std::unique_ptr<DeviceStorageStartup> Start() {
    WorkerIo = true;
    auto startup = std::make_unique<DeviceStorageStartup>(journal, marker, online, offline,
        Identity(), DeviceStorageRuntime::Policy::BoundedCompaction);
    assert(startup->Boot());
    assert(startup->CurrentState() == DeviceStorageStartup::State::Established);
    assert(startup->Runtime() && startup->ImageStorageHealthy() && !startup->RequiresService());
    assert(startup->ControlJournal().CurrentEpoch() == startup->Runtime()->ControlJournal().CurrentEpoch());
    WorkerIo = false;
    return startup;
  }
  std::unique_ptr<DeviceStorageRuntime> Open(bool ready = true) {
    WorkerIo = true;
    auto result = std::make_unique<DeviceStorageRuntime>(journal, marker, online, offline, Identity(),
        DeviceStorageRuntime::Policy::BoundedCompaction);
    assert(result->Boot() == ready);
    WorkerIo = false;
    return result;
  }
  Memory journal, marker, online, offline;
};
class Audio final : public AudioPort {
 public:
  bool Available() const override { return true; }
  bool Play(const Clip&, uint64_t) override {
    if (Busy()) return false;
    until = now + 20; ++plays; failed = false; return true;
  }
  void Cancel() override { until = now; }
  bool Busy() const override { return now < until; }
  bool Failed() const override { return failed; }
  uint64_t now = 30, until = 0;
  unsigned plays = 0;
  bool failed = false;
};

struct Rig {
  Rig(DeviceStorageRuntime& storage, unsigned boot = 1)
      : storage(storage), bootId(Id(90000 + boot)), effects(io), safetyPort(effects), safety(safetyPort),
      sequence(safety, true), control("offline-device", bootId, sequence, storage.ControlJournal(), 1),
      state(false, 0, ""), runtime(control, sequence, state, OfflineShow::CatalogVersion()),
      physical(effects, runtime), presentation(physical), bridge(storage.EvidenceRequests(), storage),
      show(runtime, state, control, presentation, physical, audio, bridge,
          "offline-device", bootId, Id(80000), &Bank) {
    calibration.fill({1100, 1900, 120});
    assert(effects.Configure(calibration, 1));
    effects.Tick(0); effects.Tick(now);
    assert(runtime.BindPresentation(presentation));
    physical.BindClock(Clock, this);
    physical.BindBootIdentity(bootId);
    Recover();
  }
  static uint64_t Clock(void* context) { return static_cast<Rig*>(context)->now; }
  bool Work() {
    WorkerIo = true;
    const bool work = storage.RunOne();
    WorkerIo = false;
    return work;
  }
  void Recover() {
    assert(effects.RecoverAfterInspection());
    for (unsigned attempt = 0; attempt != 12; ++attempt) {
      if (runtime.RecoverAfterInspection()) return;
      Work();
    }
    assert(false);
  }
  void Tick(bool worker = true) {
    now += 10; audio.now = now;
    presentation.CheckDeadline(now); effects.Tick(now);
    SelectionStep step; runtime.Tick(now, step);
    presentation.CheckDeadline(now); physical.Tick(now);
    show.Tick(now, true);
    if (worker) Work();
  }
  void Arm() {
    const uint64_t armGeneration = runtime.Generation(), armRevision = control.Revision();
    assert(!show.Arm(armGeneration, armRevision) && show.LastOperationIsPending());
    for (unsigned attempt = 0; attempt != 30; ++attempt) {
      Tick();
      if (show.Arm(armGeneration, armRevision)) { assert(show.Armed()); return; }
      assert(show.LastOperationIsPending());
    }
    assert(false);
  }
  Outbox Published() const { Outbox value; assert(bridge.Read(value)); return value; }
  Outcome Find(const std::string& id) const {
    for (const auto& record : Published().records) if (id == record.operationId.data()) return record;
    return {};
  }
  void Start(unsigned operation, bool shortVariant = false) {
    currentId = Id(operation); started = now;
    generation = runtime.Generation(); revision = control.Revision(); epoch = control.OperationEpoch();
    servoBefore = io.servoWrites; audioBefore = audio.plays;
    assert(!show.Start(currentId, 3, shortVariant, generation, revision, now, false, epoch));
    assert(show.Active() && show.LastOperationIsPending());
    assert(!physical.SelectVariant(!shortVariant));
    assert(io.servoWrites == servoBefore && audio.plays == audioBefore);
    assert(!Find(currentId).operationId[0]);
    // Polling keeps the original timestamp and variant, never a new intent.
    assert(!show.Start(currentId, 3, shortVariant, generation, revision, now + 1, false, epoch));
  }
  void ToSourceQueued() {
    assert(Work());
    Tick(false); // Adopt journal ACK and retain OfflineReserve.
    Tick(false); // Submit the typed source intent.
    assert(audio.plays == audioBefore && io.servoWrites == servoBefore);
    assert(!physical.SelectVariant(true));
    assert(!Find(currentId).operationId[0]);
  }
  void ToBeginQueued() {
    ToSourceQueued();
    assert(Work());
    for (unsigned attempt = 0; attempt != 30; ++attempt) {
      Tick(false);
      if (control.HasStorageWork()) {
        const auto record = Find(currentId);
        assert(record.operationId[0] && record.startedAtMs == started && !record.completed);
        assert(record.operationEpoch == epoch && record.deviceRevision == revision);
        assert(io.servoWrites == servoBefore);
        return;
      }
    }
    assert(false);
  }
  void ToPhysicalClose(bool proof) {
    ToBeginQueued();
    assert(Work());
    for (unsigned attempt = 0; attempt != 1600; ++attempt) {
      Tick(false);
      assert(presentation.State() != WandPresentationState::Fault);
      if (presentation.State() == WandPresentationState::Completing &&
          runtime.HasValidatedAutonomousCompletion() == proof) return;
      Work();
    }
    assert(false);
  }
  Outcome Complete(unsigned operation, bool shortVariant) {
    Start(operation, shortVariant);
    for (unsigned tick = 0; tick != 1700 && show.Active(); ++tick) Tick();
    assert(!show.Active() && presentation.State() == WandPresentationState::Idle);
    const auto record = Find(currentId);
    assert(record.completed && record.startedAtMs == started && record.completedAtMs >= started);
    assert(record.operationEpoch == epoch && record.generation == generation && record.deviceRevision == revision);
    assert(record.shortVariant == shortVariant && record.physicalFingerprint == PhysicalPresentation::OperationFingerprint(shortVariant));
    assert(io.servoWrites == servoBefore + 2);
    assert(control.WasTimedPresentationCompleted(currentId, 3, epoch, record.physicalFingerprint));
    return record;
  }
  void Ack(const Outcome& record) {
    const std::string id = record.operationId.data();
    assert(!show.Acknowledge(id, record.operationEpoch) && show.LastOperationIsPending());
    for (unsigned attempt = 0; attempt != 30; ++attempt) {
      Tick();
      if (show.Acknowledge(id, record.operationEpoch)) { assert(!Find(id).operationId[0]); return; }
      assert(show.LastOperationIsPending());
    }
    assert(false);
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
  PhysicalPresentation physical;
  WandPresentation presentation;
  Audio audio;
  OfflineEvidenceBridge bridge;
  OfflineShow show;
  std::array<ServoCalibration, 6> calibration{};
  uint64_t now = 30, started = 0, generation = 0, revision = 0, epoch = 0;
  std::string currentId;
  int servoBefore = 0;
  unsigned audioBefore = 0;
};

void ExtendedOfflineProducerLifecycle() {
  constexpr unsigned Boots = 126, PerBoot = 8;
  Durable durable;
  Outcome retained;
  uint64_t compactions = 0, simulated = 0, epoch = 0;
  for (unsigned boot = 0; boot != Boots; ++boot) {
    auto startup = durable.Start();
    auto* storage = startup->Runtime();
    Rig owner(*storage, boot + 1); owner.Arm();
    for (unsigned cycle = 0; cycle != PerBoot; ++cycle) {
      const unsigned operation = boot * PerBoot + cycle + 1;
      const auto record = owner.Complete(operation, operation % 2 == 0);
      if (operation == 1) retained = record;
      else owner.Ack(record);
      for (unsigned drain = 0; drain != 3; ++drain) owner.Tick();
      assert(owner.Find(retained.operationId.data()).completed);
      assert(storage->ControlJournal().WasTimedPresentationCompletedHistorical(retained.operationEpoch,
          retained.operationId.data(), retained.slot, retained.physicalFingerprint));
      epoch = owner.control.OperationEpoch();
    }
    compactions += storage->Compactions(); simulated += owner.now - 30;
  }
  assert(compactions >= 2 && epoch > retained.operationEpoch);
  std::printf("offline production-startup workload: cycles=%u boots=%u compactions=%llu finalEpoch=%llu simulatedMs=%llu acked=%u retained=1\n",
      Boots * PerBoot, Boots, static_cast<unsigned long long>(compactions), static_cast<unsigned long long>(epoch),
      static_cast<unsigned long long>(simulated), Boots * PerBoot - 1);
}

void StopBoundariesAndSettings() {
  for (unsigned phase = 0; phase != 5; ++phase) {
    Durable durable; auto storage = durable.Open(); Rig owner(*storage); owner.Arm();
    owner.Start(2000);
    if (phase == 1) owner.ToSourceQueued();
    if (phase == 2) owner.ToBeginQueued();
    if (phase == 3) owner.ToPhysicalClose(false);
    if (phase == 4) owner.ToPhysicalClose(true);
    owner.show.Stop();
    const auto writes = owner.io.servoWrites;
    assert(!owner.show.Active() && !owner.show.Armed() && owner.show.QuietEnabled());
    for (unsigned drain = 0; drain != 30; ++drain) owner.Tick();
    assert(owner.io.servoWrites == writes && !owner.io.run);
    const auto record = owner.Find(owner.currentId);
    assert(!record.completed);
    assert(!owner.show.Acknowledge(owner.currentId, owner.epoch) || !record.operationId[0]);
  }
  for (bool written : {false, true}) {
    Durable durable; auto storage = durable.Open(); Rig owner(*storage);
    const auto generation = owner.runtime.Generation(), revision = owner.control.Revision();
    assert(!owner.show.Arm(generation, revision));
    owner.Tick(false); // Arm is actually queued to the worker.
    if (written) assert(owner.Work());
    assert(!owner.show.ConfigureAmbient(true));
    assert(!owner.show.Quiet(true));
    assert(owner.show.QuietEnabled());
    assert(!owner.show.ApplyServerStop(7));
    assert(owner.show.ServerStopRevision() == 0 && !owner.show.Armed());
    for (unsigned drain = 0; drain != 40; ++drain) owner.Tick();
    const auto publication = owner.Published();
    assert(publication.serverStopRevision == 7 && publication.quiet && !publication.offlineEnabled && !publication.ambientEnabled);
    assert(owner.show.ApplyServerStop(7));
    assert(!owner.show.Armed() && owner.show.QuietEnabled() && !owner.show.AmbientEnabled());
    assert(!owner.show.Arm(generation, revision));
  }
}

void AdmissionAndSourceCutsHoldIncomplete() {
  for (bool source : {false, true}) for (bool after : {false, true}) {
    Durable durable; auto storage = durable.Open();
    {
      Rig owner(*storage); owner.Arm(); owner.Start(3000);
      if (source) owner.ToSourceQueued();
      auto& cut = source ? durable.offline : durable.journal;
      cut.cutBefore = !after; cut.cutAfter = after;
      assert(owner.Work());
      for (unsigned drain = 0; drain != 6; ++drain) owner.Tick();
      assert(owner.io.servoWrites == owner.servoBefore && !owner.show.Armed());
      assert(!owner.runtime.HasValidatedAutonomousCompletion());
    }
    storage.reset();
    durable.offline.cutBefore = durable.offline.cutAfter = durable.journal.cutBefore = durable.journal.cutAfter = false;
    auto reopened = durable.Open(); Rig recovered(*reopened, 2);
    const auto record = recovered.Find(Id(3000));
    assert(!record.completed);
    assert(!reopened->ControlJournal().WasTimedPresentationCompletedHistorical(1, Id(3000), 3,
        PhysicalPresentation::OperationFingerprint(false)));
    assert(!record.operationId[0] || !recovered.show.Acknowledge(Id(3000), record.operationEpoch));
  }
}

void PersistedArmRetainsItsFirstObserverFence() {
  Durable durable;
  auto storage = durable.Open();
  { Rig owner(*storage); owner.Arm(); }
  storage.reset();
  auto reopened = durable.Open();
  Rig resumed(*reopened, 2);
  Outbox before, after;
  uint64_t beforeGeneration = 0, afterGeneration = 0;
  assert(reopened->EvidenceRequests().TryReadOffline(before, &beforeGeneration));
  for (unsigned tick = 0; tick != 30 && !resumed.show.Armed(); ++tick) resumed.Tick();
  assert(resumed.show.Armed());
  for (unsigned tick = 0; tick != 10; ++tick) resumed.Tick();
  assert(reopened->EvidenceRequests().TryReadOffline(after, &afterGeneration));
  assert(afterGeneration == beforeGeneration + 1 && after.offlineEnabled);
}

void OriginalPreparationAndBeginDeadlines() {
  for (unsigned failure = 0; failure != 3; ++failure) {
    Durable durable; auto storage = durable.Open(); Rig owner(*storage); owner.Arm();
    owner.Start(4000);
    if (failure == 0) {
      for (unsigned tick = 0; tick != 1852; ++tick) owner.Tick(false);
    } else if (failure == 1) {
      owner.ToBeginQueued();
      for (unsigned tick = 0; tick != 202; ++tick) owner.Tick(false);
    } else {
      assert(owner.effects.Configure(owner.calibration, 2));
      owner.Tick(false);
    }
    assert(!owner.show.Active() && !owner.show.Armed());
    assert(owner.io.servoWrites == owner.servoBefore);
    for (unsigned drain = 0; drain != 25; ++drain) owner.Tick();
    const auto record = owner.Find(owner.currentId);
    assert(!record.completed && !owner.runtime.HasValidatedAutonomousCompletion());
    assert(!record.operationId[0] || !owner.show.Acknowledge(owner.currentId, owner.epoch));
  }
}

void CompletionTimestampPrecedesSourceAckWait() {
  Durable durable; auto storage = durable.Open(); Rig owner(*storage); owner.Arm();
  owner.Start(5000);
  owner.ToPhysicalClose(true);
  assert(owner.Work()); // Timed proof commits; the physical owner consumes ACK next.
  owner.Tick(false);
  assert(owner.presentation.State() == WandPresentationState::Idle && owner.show.Active());
  const uint64_t completedAt = owner.now;
  const auto endingBefore = owner.audio.plays;
  for (unsigned delay = 0; delay != 100; ++delay) owner.Tick(false);
  assert(owner.show.Active() && !owner.Find(owner.currentId).completed);
  assert(owner.audio.plays == endingBefore);
  assert(owner.Work());
  for (unsigned adoption = 0; adoption != 3 && owner.show.Active(); ++adoption) owner.Tick(false);
  const auto record = owner.Find(owner.currentId);
  assert(!owner.show.Active() && record.completed && record.completedAtMs == completedAt);
  assert(owner.audio.plays == endingBefore + 1);
}
} // namespace

int main() {
  ExtendedOfflineProducerLifecycle();
  StopBoundariesAndSettings();
  AdmissionAndSourceCutsHoldIncomplete();
  PersistedArmRetainsItsFirstObserverFence();
  OriginalPreparationAndBeginDeadlines();
  CompletionTimestampPrecedesSourceAckWait();
}

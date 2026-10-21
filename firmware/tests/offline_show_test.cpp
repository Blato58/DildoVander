#include "physical_effects_test_support.h"
#include "../src/features/offline_show/OfflineShow.h"
#include "../src/features/offline_show/ImaAdpcm.h"
#include <cstring>
#include <functional>
#include <iostream>
#include <vector>
#include "../src/features/device_control/PersistentCommandJournal.h"

using namespace kufr::offline;
namespace {
const char* Boot = "d1bcbf5e-5c07-4f63-8fca-ff5988af6bca";
const char* Evening = "de5c1e90-ec5a-4529-82ee-f5e871de1059";
const uint8_t Samples[] = {0, 0};
const Clip Clips[] = {
    {"intro-1", "test", Samples, 2, 1, "hash", 0},
    {"intro-2", "test", Samples, 2, 1, "hash", 0},
    {"ending-1", "test", Samples, 2, 1, "hash", 0},
    {"ending-2", "test", Samples, 2, 1, "hash", 0},
    {"slot-1", "test", Samples, 2, 1, "hash", 1},
    {"slot-2", "test", Samples, 2, 1, "hash", 2},
    {"slot-3", "test", Samples, 2, 1, "hash", 3},
    {"slot-4", "test", Samples, 2, 1, "hash", 4},
    {"slot-5", "test", Samples, 2, 1, "hash", 5},
    {"slot-6", "test", Samples, 2, 1, "hash", 6},
    {"ambient-1", "test", Samples, 2, 1, "hash", 0},
    {"ambient-2", "test", Samples, 2, 1, "hash", 0},
    {"ambient-3", "test", Samples, 2, 1, "hash", 0},
    {"ambient-4", "test", Samples, 2, 1, "hash", 0},
    {"ambient-5", "test", Samples, 2, 1, "hash", 0},
    {"ambient-6", "test", Samples, 2, 1, "hash", 0},
    {"ambient-7", "test", Samples, 2, 1, "hash", 0},
    {"ambient-8", "test", Samples, 2, 1, "hash", 0},
    {"ambient-9", "test", Samples, 2, 1, "hash", 0},
    {"ambient-10", "test", Samples, 2, 1, "hash", 0},
    {"ambient-11", "test", Samples, 2, 1, "hash", 0},
    {"ambient-12", "test", Samples, 2, 1, "hash", 0},
};
const BankView Bank{Clips, sizeof(Clips) / sizeof(Clip), 36, 1500000, true,
    "test", "ollivander-private-party-v1", "marin"};
struct Audio final : AudioPort {
  bool busy = false, fail = false, available = true;
  int plays = 0, cancels = 0;
  std::string lastClip;
  bool Available() const override { return available; }
  bool Play(const Clip& clip, uint64_t) override {
    if (busy || !available) return false;
    busy = true; fail = false; ++plays; lastClip = clip.id; return true;
  }
  void Cancel() override { if (busy) { ++cancels; busy = false; fail = true; } }
  bool Busy() const override { return busy; }
  bool Failed() const override { return fail; }
};
struct Storage final : StoragePort {
  Outbox saved{};
  bool fail = false;
  std::function<void(const Outbox&)> beforeWrite;
  bool Read(Outbox& outbox) override { outbox = saved; return !fail; }
  bool Write(const Outbox& outbox) override {
    if (beforeWrite) beforeWrite(outbox);
    if (fail) return false;
    saved = outbox; return true;
  }
};
struct Rig {
  FakeIo io;
  PhysicalEffects effects{io};
  SafetyAdapter port{effects};
  SafetyController safety{port};
  SelectionSequence sequence{safety, true};
  Journal journal;
  DeviceControl control{"device", Boot, sequence, journal, 1};
  ConversationRuntimeState state{false, 0, ""};
  DeviceControlRuntime runtime{control, sequence, state, OfflineShow::CatalogVersion()};
  PhysicalPresentation physical{effects, runtime};
  WandPresentation presentation{physical};
  Audio audio;
  Storage storage;
  OfflineShow offline{runtime, state, control, presentation, physical, audio, storage,
      "device", Boot, Evening, &Bank};
  uint64_t now = 30;
  Rig() {
    std::array<ServoCalibration, 6> calibration;
    calibration.fill({1100, 1900, 500});
    assert(effects.Configure(calibration, 1));
    effects.Tick(0); effects.Tick(now);
    assert(effects.RecoverAfterInspection());
    assert(runtime.BindPresentation(presentation));
    assert(runtime.RecoverAfterInspection());
  }
  void Arm() { assert(offline.Arm(runtime.Generation(), control.Revision())); }
  std::string Id(unsigned number) {
    return "00000000-0000-4000-8000-0000000000" + std::to_string(number / 10) + std::to_string(number % 10);
  }
  void Tick(bool idle = true) {
    now += 10;
    presentation.CheckDeadline(now); effects.Tick(now);
    SelectionStep step;
    runtime.Tick(now, step);
    physical.Tick(now);
    offline.Tick(now, idle);
    for (uint8_t value : io.pixels) assert(value <= 64);
  }
  bool Start(unsigned number, uint8_t slot, bool shortened = false) {
    return offline.Start(Id(number), slot, shortened, runtime.Generation(), control.Revision(), now);
  }
  void Finish() {
    const uint64_t deadline = now + 16000;
    while (offline.Active()) { assert(now < deadline); audio.busy = false; Tick(); }
    audio.busy = false;
    Tick();
  }
};

void AllSlotsAndVariantsUseJournalAndIdempotentOutbox() {
  Rig f; f.Arm();
  for (unsigned slot = 1; slot <= 6; ++slot) {
    const auto generation = f.runtime.Generation();
    assert(f.Start(slot, static_cast<uint8_t>(slot), false));
    assert(f.io.servoWrites == static_cast<int>((slot - 1) * 2));
    assert(!f.storage.saved.records[slot - 1].completed);
    assert(f.offline.Start(f.Id(slot), static_cast<uint8_t>(slot), false,
        generation, f.control.Revision(), f.now));
    assert(!f.offline.Start(f.Id(slot), static_cast<uint8_t>(slot % 6 + 1), false,
        generation, f.control.Revision(), f.now));
    f.Finish();
    assert(f.journal.begins == static_cast<int>(slot) && f.journal.restores == static_cast<int>(slot));
    assert(f.storage.saved.records[slot - 1].completed);
    assert(f.control.ReadyMask() == 63);
  }
  assert(f.offline.OutboxJson().find("completed_by_timing") != std::string::npos);
  assert(f.offline.Acknowledge(f.Id(1)) && f.offline.Acknowledge(f.Id(1)));
  assert(f.offline.OutboxJson().find(f.Id(1)) == std::string::npos);
  for (unsigned slot = 2; slot <= 6; ++slot) assert(f.offline.Acknowledge(f.Id(slot)));
  for (unsigned slot = 1; slot <= 6; ++slot) {
    assert(f.Start(10 + slot, static_cast<uint8_t>(slot), true));
    f.Finish();
    assert(f.offline.Acknowledge(f.Id(10 + slot)));
  }
  assert(f.journal.begins == 12 && f.journal.restores == 12);
}

void StopFailureCapacityAndEpochFence() {
  {
    Rig f;
    assert(f.runtime.AcquireManualControl("lease", f.now));
    const auto oldGeneration = f.runtime.Generation();
    assert(f.runtime.HandoffToOffline("lease", f.now));
    assert(!f.control.HasManualControl() && f.runtime.Generation() > oldGeneration);
    f.Arm();
    assert(!f.offline.Start(f.Id(1), 1, false, oldGeneration, f.control.Revision(), f.now));
    ConversationRuntimeSnapshot snapshot;
    assert(f.state.Read(snapshot));
    assert(!f.runtime.BeginAutonomousPresentation(f.Id(1), 1, snapshot));
    assert(f.Start(1, 1));
    f.offline.Stop();
    assert(f.control.NeedsRecovery() && f.io.servoWrites == 0);
    assert(f.offline.OutboxJson() == "[]" && !f.offline.Acknowledge(f.Id(1)));
    assert(!f.offline.Armed());
  }
  {
    Rig f; f.Arm(); assert(f.Start(1, 1)); f.audio.fail = true; f.audio.busy = false; f.Tick();
    assert(!f.offline.Active() && f.journal.begins == 0 && f.control.NeedsRecovery());
  }
  {
    Rig f; f.Arm(); assert(f.Start(1, 1));
    for (unsigned count = 0; count < 400; ++count) { f.audio.busy = false; f.Tick(); }
    assert(f.journal.pending && f.io.servoWrites > 0);
    f.offline.Stop();
    assert(f.journal.pending && f.offline.OutboxJson() == "[]");
    assert(std::string(f.offline.Preflight()) == "recovery_required");
  }
  {
    Rig f; f.Arm(); f.storage.fail = true;
    assert(!f.Start(1, 1) && f.journal.begins == 0 && f.io.servoWrites == 0);
  }
  {
    Rig f; f.Arm();
    for (unsigned count = 1; count <= 8; ++count) { assert(f.Start(count, 1)); f.Finish(); }
    assert(std::string(f.offline.Preflight()) == "audio_only");
    assert(!f.Start(9, 1) && f.journal.begins == 8);
    OfflineShow reboot(f.runtime, f.state, f.control, f.presentation, f.physical,
        f.audio, f.storage, "device", Boot, "ec4c7df8-358c-4c5a-9235-d5100c2e2141", &Bank);
    assert(reboot.OutboxJson() == f.offline.OutboxJson());
    assert(reboot.OutboxJson().find(Evening) != std::string::npos);
  }
}

void AmbientThirtyMinutesAndQuiet() {
  Rig f;
  assert(f.offline.ConfigureAmbient(true)); // Online idle; no offline authority needed.
  std::array<uint64_t, 20> spoken{};
  size_t count = 0;
  int observed = 0;
  while (f.now < 1800000) {
    f.audio.busy = false;
    f.Tick();
    if (f.audio.plays != observed) {
      observed = f.audio.plays;
      assert(count < spoken.size()); spoken[count++] = f.now;
    }
  }
  assert(count >= 6 && count <= 9 && f.io.servoWrites == 0 && f.io.vibrationWrites == 0);
  for (size_t index = 0; index < count; ++index) {
    if (index) assert(spoken[index] - spoken[index - 1] >= 180000);
    if (index >= 3) assert(spoken[index] - spoken[index - 3] >= 600000);
  }
  assert(f.offline.Quiet(true));
  const int plays = f.audio.plays;
  for (unsigned index = 0; index < 20000; ++index) f.Tick();
  assert(f.audio.plays == plays && !f.runtime.HasDecoration());
}

void AmbientRequiresExplicitOptInAndStopRevokesIt() {
  Rig f;
  for (unsigned index = 0; index < 20000; ++index) f.Tick();
  assert(f.audio.plays == 0 && f.io.lightWrites == 0);
  assert(f.offline.ConfigureAmbient(true));
  for (unsigned index = 0; index < 12100; ++index) f.Tick();
  assert(f.audio.plays == 1 && f.audio.busy);
  assert(f.offline.PrepareInteraction());
  assert(!f.audio.busy && !f.runtime.HasDecoration());
  f.offline.Stop();
  assert(!f.storage.saved.ambientEnabled && f.storage.saved.quiet && !f.storage.saved.offlineEnabled);
  assert(f.runtime.RecoverAfterInspection());
  assert(f.offline.Quiet(false));
  const int plays = f.audio.plays;
  for (unsigned index = 0; index < 20000; ++index) f.Tick();
  assert(f.audio.plays == plays); // Quiet off alone cannot restore ambient mode.
}

void AdpcmFramingAndChunkedDecode() {
  const uint8_t encoded[] = {0,0,0,0,0x10,0x32,0x54,0x76,0x98,0xba,0xdc,0xfe};
  const int expected[] = {0,0,1,4,8,15,27,47,88,82,66,41,10,-28,-84,-181,-380};
  ImaAdpcm decoder;
  assert(decoder.Begin(encoded, sizeof(encoded), 17));
  uint8_t pcm[2];
  assert(decoder.Read(pcm, 1) == 0);
  for (int sample : expected) {
    assert(decoder.Read(pcm, sizeof(pcm)) == 2);
    assert(static_cast<int16_t>(pcm[0] | (pcm[1] << 8)) == sample);
  }
  assert(decoder.Read(pcm, sizeof(pcm)) == 0);
  assert(!decoder.Begin(encoded, sizeof(encoded) - 1, 17));
  assert(!decoder.Begin(encoded, sizeof(encoded), 0));
  assert(!decoder.Begin(encoded, sizeof(encoded), 192001));
  const uint8_t invalid[] = {0,0,89,0};
  assert(!decoder.Begin(invalid, sizeof(invalid), 1));
}

void ScriptedStandaloneAndVoiceReadiness() {
  Rig f;
  f.offline.SetVoice("cedar");
  assert(std::string(f.offline.Preflight()) == "missing_assets");
  assert(f.offline.StatusJson().find("\"bankVoice\":\"marin\"") != std::string::npos);
  assert(f.offline.StatusJson().find("\"voiceCompatible\":false") != std::string::npos);
  assert(!f.offline.Arm(f.runtime.Generation(), f.control.Revision()));
  f.offline.SetVoice("marin"); f.Arm();
  for (unsigned slot = 1; slot <= 6; ++slot) {
    assert(f.offline.StartNext(f.now));
    f.Finish();
    const auto& record = f.storage.saved.records[slot - 1];
    assert(record.scripted && record.slot == slot && record.completed);
    assert(std::string(record.localEveningId.data()) == Evening);
    if (slot > 1) assert(record.operationId != f.storage.saved.records[slot - 2].operationId);
  }
  assert(f.offline.OutboxJson().find("offline_scripted") != std::string::npos);
  OfflineShow missing(f.runtime, f.state, f.control, f.presentation, f.physical,
      f.audio, f.storage, "device", Boot, Evening);
  assert(std::string(missing.Preflight()) == "missing_assets");
  assert(!missing.Arm(f.runtime.Generation(), f.control.Revision()));
}

class EpochStorage final : public JournalStorage {
 public:
  JournalReadResult Read(uint8_t* target, size_t capacity, size_t& length) override {
    length = bytes.size();
    if (bytes.empty()) return JournalReadResult::Missing;
    if (length > capacity) return JournalReadResult::Failed;
    std::copy(bytes.begin(), bytes.end(), target);
    return JournalReadResult::Found;
  }
  bool Write(const uint8_t* source, size_t length) override {
    bytes.assign(source, source + length); return true;
  }
  std::vector<uint8_t> bytes;
};
void PendingAudioCannotRestampOperationEpoch() {
  FakeIo io; PhysicalEffects effects(io); SafetyAdapter port(effects);
  SafetyController safety(port); SelectionSequence sequence(safety, true);
  EpochStorage journalStorage;
  PersistentCommandJournal journal(journalStorage);
  assert(journal.PersistReadyMask(63) && journal.EstablishScopedIdentity("device", true));
  DeviceControl control("device", Boot, sequence, journal, 1);
  ConversationRuntimeState state(false, 0, "");
  DeviceControlRuntime runtime(control, sequence, state, OfflineShow::CatalogVersion());
  PhysicalPresentation physical(effects, runtime); WandPresentation presentation(physical);
  Audio audio; Storage storage;
  storage.beforeWrite = [&](const Outbox& next) {
    for (const auto& record : next.records) if (record.operationId[0]) {
      assert(journal.IsEvidencePinned(record.operationEpoch, record.operationId.data(),
          record.slot, record.physicalFingerprint));
    }
  };
  std::array<ServoCalibration, 6> calibration; calibration.fill({1100, 1900, 500});
  assert(effects.Configure(calibration, 1));
  assert(runtime.BindPresentation(presentation) && runtime.RecoverAfterInspection());
  OfflineShow offline(runtime, state, control, presentation, physical, audio, storage,
      "device", Boot, Evening, &Bank);
  const std::string id = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
  assert(offline.Arm(runtime.Generation(), control.Revision()));
  assert(!offline.Start(id, 1, false, runtime.Generation(), control.Revision(), 30));
  assert(offline.Start(id, 1, false, runtime.Generation(), control.Revision(), 30, false, 1));
  assert(storage.saved.records[0].operationEpoch == 1 && storage.saved.records[0].physicalFingerprint
      == PhysicalPresentation::OperationFingerprint(false));
  assert(journal.HasPresentationReservation() && !journal.HasPending() && journal.HistoryCount() == 1);
  journal.CancelPresentationReservation();
  assert(journal.AdvanceEpochAndCompact());
  runtime.PublishCurrent();
  audio.busy = false;
  offline.Tick(40, true); // Intro may finish, but the saved admission epoch remains one.
  audio.busy = false;
  offline.Tick(50, true);
  assert(!journal.HasPending() && journal.HistoryCount() == 1 && !offline.Active());
  assert(io.servoWrites == 0);
  assert(storage.saved.records[0].operationEpoch == 1 && !storage.saved.records[0].completed);
  assert(storage.saved.records[0].operationId[0] && offline.OutboxJson() == "[]");
}
void LegacyPodMigrationAndEpochAcknowledgement() {
  storage::LegacyOutboxV1 legacy;
  legacy.offlineEnabled = true; legacy.ambientEnabled = true; legacy.quiet = true;
  legacy.serverSettingsRevision = 17; legacy.serverStopRevision = 19;
  for (size_t index = 0; index < 2; ++index) {
    auto& record = legacy.records[index];
    const char* id = index ? "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa" : "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    std::copy(id, id + 37, record.operationId.begin());
    std::copy(Boot, Boot + 37, record.bootId.begin());
    std::copy(Evening, Evening + 37, record.localEveningId.begin());
    record.generation = 23; record.startedAtMs = 100; record.completedAtMs = index ? 0 : 200;
    record.slot = static_cast<uint8_t>(index + 1); record.completed = index == 0;
    record.scripted = true; record.shortVariant = true;
  }
  Outbox migrated;
  assert(storage::DecodeOutboxBytes(&legacy, sizeof(legacy), migrated));
  assert(migrated.version == 2 && migrated.offlineEnabled && migrated.ambientEnabled && migrated.quiet);
  assert(migrated.serverSettingsRevision == 17 && migrated.serverStopRevision == 19);
  for (size_t index = 0; index < 2; ++index) {
    const auto& original = legacy.records[index]; const auto& record = migrated.records[index];
    assert(record.operationId == original.operationId && record.bootId == original.bootId
        && record.localEveningId == original.localEveningId);
    assert(record.generation == 23 && record.startedAtMs == 100 && record.completedAtMs == original.completedAtMs);
    assert(record.completed == original.completed && record.scripted && record.shortVariant);
    assert(record.operationEpoch == 0 && record.physicalFingerprint == 0 && record.deviceRevision == 0);
  }
  Outbox roundtrip;
  assert(storage::DecodeOutboxBytes(&migrated, sizeof(migrated), roundtrip));
  assert(roundtrip.records[1].operationId == legacy.records[1].operationId && !roundtrip.records[1].completed);
  assert(!storage::DecodeOutboxBytes(&legacy, sizeof(legacy) - 1, roundtrip));
  legacy.version = 2;
  assert(!storage::DecodeOutboxBytes(&legacy, sizeof(legacy), roundtrip));
  Rig f;
  f.storage.saved = migrated;
  f.storage.saved.records[2] = migrated.records[0];
  f.storage.saved.records[2].operationEpoch = 9007199254740993ull;
  f.storage.saved.records[2].physicalFingerprint = 101;
  OfflineShow rebooted(f.runtime, f.state, f.control, f.presentation, f.physical,
      f.audio, f.storage, "device", Boot, Evening, &Bank);
  const auto id = std::string(migrated.records[0].operationId.data());
  const auto legacyResponse = rebooted.OutboxJson();
  assert(legacyResponse.find("operationEpoch") == std::string::npos);
  assert(legacyResponse.find(id) != std::string::npos); // The epoch-zero record is still visible.
  assert(rebooted.OutboxJson(true).find("\"operationEpoch\":\"9007199254740993\"") != std::string::npos);
  assert(rebooted.Acknowledge(id));
  assert(!f.storage.saved.records[0].operationId[0] && f.storage.saved.records[2].operationId[0]);
  assert(rebooted.OutboxJson() == "[]");
  assert(rebooted.OutboxJson(true).find(id) != std::string::npos);
  assert(rebooted.Acknowledge(id)); // Replaying an unaware ACK cannot remove the scoped record.
  assert(f.storage.saved.records[2].operationId[0]);
  assert(rebooted.Acknowledge(id, 9007199254740992ull)); // Foreign ACK is a harmless replay.
  assert(f.storage.saved.records[2].operationId[0]);
  f.storage.fail = true;
  assert(!rebooted.Acknowledge(id, 9007199254740993ull));
  assert(f.storage.saved.records[2].operationId[0]);
  f.storage.fail = false;
  OfflineShow afterFailedWrite(f.runtime, f.state, f.control, f.presentation, f.physical,
      f.audio, f.storage, "device", Boot, Evening, &Bank);
  assert(afterFailedWrite.Acknowledge(id, 9007199254740993ull));
  assert(!f.storage.saved.records[2].operationId[0] && f.storage.saved.records[1].operationId[0]);
  assert(!afterFailedWrite.Acknowledge(f.storage.saved.records[1].operationId.data()));
}
}

int main() {
  PendingAudioCannotRestampOperationEpoch();
  LegacyPodMigrationAndEpochAcknowledgement();
  AllSlotsAndVariantsUseJournalAndIdempotentOutbox();
  StopFailureCapacityAndEpochFence();
  AmbientThirtyMinutesAndQuiet();
  AmbientRequiresExplicitOptInAndStopRevokesIt();
  AdpcmFramingAndChunkedDecode();
  ScriptedStandaloneAndVoiceReadiness();
  std::cout << "offline core: six slots, variants, epoch, Stop, outbox, 30-minute ambient passed\n";
}

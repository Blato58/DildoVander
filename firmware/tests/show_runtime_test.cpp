#include "physical_effects_test_support.h"
#include "../src/features/scene_intent/SceneIntentBridge.h"
#include "../src/features/device_control/PersistentCommandJournal.h"
#include <cstring>
#include <type_traits>

struct ClockedJournalStorage final : JournalStorage {
  uint64_t nowMs = 30, nextWriteDelayMs = 0, nextWriteReturnsAtMs = 0;
  bool present = false;
  std::array<uint8_t, PersistentCommandJournal::RecordSize> bytes{};
  ClockedJournalStorage() {
    // Inventory was provisioned before the control owner reads its boot state.
    PersistentCommandJournal provisioned(*this);
    assert(provisioned.PersistReadyMask(63));
  }
  JournalReadResult Read(uint8_t* destination, size_t capacity, size_t& length) override {
    if (!present) return JournalReadResult::Missing;
    assert(capacity >= bytes.size());
    std::copy(bytes.begin(), bytes.end(), destination);
    length = bytes.size();
    return JournalReadResult::Found;
  }
  bool Write(const uint8_t* source, size_t length) override {
    assert(length == bytes.size());
    std::copy(source, source + length, bytes.begin());
    present = true;
    nowMs = nextWriteReturnsAtMs ? nextWriteReturnsAtMs : nowMs + nextWriteDelayMs;
    nextWriteDelayMs = nextWriteReturnsAtMs = 0;
    return true;
  }
};

struct ClockedJournalFixture {
  FakeIo io;
  PhysicalEffects effects{io};
  SafetyAdapter port{effects};
  SafetyController safety{port};
  SelectionSequence sequence{safety, true};
  ClockedJournalStorage storage;
  PersistentCommandJournal journal{storage};
  DeviceControl control{"device", "boot", sequence, journal, 1};
  ConversationRuntimeState state{false, 0, ""};
  DeviceControlRuntime runtime{control, sequence, state, "catalog"};
  PhysicalPresentation adapter{effects, runtime};
  WandPresentation presentation{adapter};
  ClockedJournalFixture() {
    std::array<ServoCalibration, 6> calibration;
    calibration.fill({1100, 1900, 500});
    assert(effects.Configure(calibration, 1));
    effects.Tick(0); effects.Tick(30);
    assert(effects.RecoverAfterInspection());
    assert(runtime.BindPresentation(presentation));
    assert(runtime.RecoverAfterInspection());
    adapter.BindClock([](void* context) -> uint64_t {
      return static_cast<ClockedJournalStorage*>(context)->nowMs;
    }, &storage);
  }
  bool Start() {
    ConversationRuntimeSnapshot snapshot;
    assert(state.Read(snapshot));
    TurnProposal proposal;
    proposal.proposalId = "523ef590-ce4d-4901-91b3-79de1bfe7e35";
    proposal.compartment = 3; proposal.catalogVersion = snapshot.catalogVersion;
    proposal.generation = snapshot.generation; proposal.deviceRevision = snapshot.deviceRevision;
    PendingProposal pending;
    assert(pending.Store(proposal, snapshot, state, storage.nowMs, 30000));
    adapter.Tick(storage.nowMs);
    return presentation.Start(pending, state, storage.nowMs);
  }
  void Tick(uint64_t nowMs) {
    storage.nowMs = nowMs;
    presentation.CheckDeadline(nowMs); effects.Tick(nowMs);
    SelectionStep step;
    runtime.Tick(nowMs, step);
    presentation.CheckDeadline(storage.nowMs);
    adapter.Tick(storage.nowMs);
  }
};

void JournalLatencyUsesActualOwnerClock() {
  struct Audio final : PresentationAudioPort {
    int cues = 0;
    void Cue(uint16_t, uint16_t) override { ++cues; }
    void StopCues() override {}
  };
  {
    ClockedJournalFixture f;
    Audio audio; f.adapter.BindAudio(audio);
    f.storage.nextWriteDelayMs = 50;
    assert(f.Start());
    assert(f.storage.nowMs == 80 && f.io.lightWrites == 1 && audio.cues == 0);
    const auto& trace = f.adapter.Diagnostics();
    assert(trace.events[trace.first].timeMs == 80);
    assert(trace.events[(trace.first + 1) % trace.Capacity].dueAt == 30);
    f.adapter.Tick(30); // A stale same-pass argument must use the bound clock.
    assert(f.presentation.State() == WandPresentationState::Raising && f.effects.Healthy());
    for (uint64_t tick = 90; tick < 3550; tick += 10) f.Tick(tick);
    assert(f.io.servoWrites == 0);
    f.Tick(3550);
    assert(f.io.servoWrites == 1); // The original epoch was not rebased by NVS.
  }
  for (uint64_t delay : {uint64_t{250}, uint64_t{600}, uint64_t{15000}}) {
    ClockedJournalFixture f;
    Audio audio; f.adapter.BindAudio(audio);
    f.storage.nextWriteDelayMs = delay;
    assert(!f.Start());
    assert(f.presentation.State() == WandPresentationState::Fault);
    assert(f.io.lightWrites == 2 && f.io.servoWrites == 0 && f.io.vibrationWrites == 0 && audio.cues == 0);
    for (uint8_t value : f.io.pixels) assert(value == 0);
    assert(f.journal.HasPending() && f.control.NeedsRecovery());
    ConversationRuntimeSnapshot snapshot;
    assert(!f.state.Read(snapshot));
    if (delay == 15000) {
      const auto& trace = f.adapter.Diagnostics();
      assert(std::string(trace.events[(trace.first + trace.count - 1) % trace.Capacity].reason)
          == "journal-deadline-exceeded");
    }
  }
  {
    ClockedJournalFixture f;
    assert(f.Start());
    f.storage.nextWriteDelayMs = 50;
    for (uint64_t tick = 40; f.presentation.State() != WandPresentationState::Idle; tick += 10) {
      assert(tick < 12000); f.Tick(tick);
    }
    assert(!f.journal.HasPending() && !f.control.NeedsRecovery());
    assert(std::string(f.adapter.Diagnostics().state) == "completed");
    const auto& trace = f.adapter.Diagnostics();
    assert(trace.events[(trace.first + trace.count - 1) % trace.Capacity].timeMs == f.storage.nowMs);
    ConversationRuntimeSnapshot snapshot;
    assert(f.state.Read(snapshot));
  }
  for (bool hardDeadline : {false, true}) {
    ClockedJournalFixture f;
    assert(f.Start());
    if (hardDeadline) f.storage.nextWriteReturnsAtMs = 30 + WandPresentation::MaximumDurationMs;
    else f.storage.nextWriteDelayMs = 250;
    for (uint64_t tick = 40; f.presentation.State() != WandPresentationState::Fault; tick += 10) {
      assert(tick < 12000); f.Tick(tick);
    }
    assert(!f.journal.HasPending() && f.journal.HistoryCount() == 1);
    assert(f.control.NeedsRecovery() && !f.effects.Healthy() && !f.io.run);
    assert(std::string(f.adapter.Diagnostics().state) == "fault");
    const auto& trace = f.adapter.Diagnostics();
    for (size_t index = 0; index < trace.count; ++index)
      assert(std::string(trace.events[(trace.first + index) % trace.Capacity].kind) != "completed");
    assert(trace.events[(trace.first + trace.count - 1) % trace.Capacity].timeMs == f.storage.nowMs);
    assert(!f.runtime.AcquireManualControl("late-cleanup", f.storage.nowMs));
    ConversationRuntimeSnapshot snapshot;
    assert(!f.state.Read(snapshot));
    assert(f.effects.RecoverAfterInspection());
    assert(f.runtime.RecoverAfterInspection());
    assert(f.state.Read(snapshot));
  }
  {
    ClockedJournalFixture f;
    assert(f.Start());
    bool delayedCleanup = false;
    for (uint64_t tick = 40; f.presentation.State() != WandPresentationState::Fault; tick += 10) {
      assert(tick < 12000);
      if (f.adapter.IsResting(3)) {
        f.storage.nextWriteDelayMs = 250;
        f.io.lightsContext = &f.storage;
        f.io.lightsCallback = [](void* context) {
          auto& storage = *static_cast<ClockedJournalStorage*>(context);
          storage.nowMs += storage.nextWriteDelayMs;
          storage.nextWriteDelayMs = 0;
        };
        delayedCleanup = true;
      }
      f.Tick(tick);
    }
    assert(delayedCleanup && f.journal.HasPending() && f.control.NeedsRecovery());
    const auto& trace = f.adapter.Diagnostics();
    assert(trace.events[(trace.first + trace.count - 1) % trace.Capacity].timeMs == f.storage.nowMs);
    assert(std::string(trace.state) == "fault");
    ConversationRuntimeSnapshot snapshot;
    assert(!f.state.Read(snapshot));
  }
}

void AbsoluteClockAndNoBurst() {
  Fixture f;
  assert(f.Start("absolute"));
  for (uint64_t t = 127; t < 4100; t += 97) f.Tick(t);
  assert(f.io.servoWrites == 1);
  const auto& report = f.adapter.Diagnostics();
  bool skipped = false;
  for (size_t i = 0; i < report.count; ++i) {
    const auto& event = report.events[(report.first + i) % report.Capacity];
    if (std::strcmp(event.reason, "late-vibration") == 0) skipped = true;
    if (std::strcmp(event.phaseId, "main-001") == 0 && std::strcmp(event.kind, "commanded") == 0)
      assert(event.dueAt == 630); // Original epoch, not rounded preceding tick.
  }
  assert(skipped && report.reduced);
}

void SnapshotStopAndRing() {
  static_assert(std::is_trivially_copyable<PresentationDiagnostics>::value, "mailbox copy must not allocate");
  Fixture f;
  f.adapter.BindBootIdentity("synthetic-host");
  assert(f.Start("snapshot"));
  const auto snapshot = f.adapter.Diagnostics();
  assert(snapshot.calibrationId == 1 && snapshot.slot == 3);
  assert(std::string(snapshot.sceneHash) == PresentationScore::MainSceneHash);
  assert(std::string(snapshot.variant) == "main");
  assert(std::string(snapshot.bootId.data()) == "synthetic-host");
  assert(!f.adapter.CancelOperation("other", snapshot.generation));
  assert(!f.adapter.CancelOperation("snapshot", snapshot.generation + 1));
  assert(f.adapter.CancelOperation("snapshot", snapshot.generation));
  const int writes = f.io.servoWrites + f.io.vibrationWrites;
  for (uint64_t t = 40; t <= 6000; t += 10) f.Tick(t);
  assert(f.io.servoWrites + f.io.vibrationWrites == writes && f.journal.pending);
  assert(std::string(f.adapter.Diagnostics().state) == "fault");
  assert(f.adapter.Diagnostics().calibrationId == snapshot.calibrationId);
  assert(std::string(f.adapter.Diagnostics().sceneHash) == snapshot.sceneHash);
  assert(std::string(f.adapter.Diagnostics().variant) == snapshot.variant);

  PresentationDiagnostics ring;
  for (unsigned i = 0; i < 90; ++i) ring.Append(PresentationTraceEvent{});
  assert(ring.count == 64 && ring.droppedEvents == 26);
  assert(ring.events[ring.first].sequence == 27 && ring.lastSequence == 90);
  ring.state = "completed"; ring.evidence = "timed-complete";
  const std::string json = ExportPresentationTrace(ring);
  assert(json.find("\"droppedEvents\":26") != std::string::npos);
  assert(json.find("\"evidence\":\"timed-complete\"") != std::string::npos);
}

void LateMotionNeverStarts() {
  struct LightWrites {
    FakeIo* io;
    int nonBlackFrames = 0;
    static void Observe(void* context) {
      auto& writes = *static_cast<LightWrites*>(context);
      for (uint8_t value : writes.io->pixels) {
        if (value) { ++writes.nonBlackFrames; break; }
      }
    }
  };
  Fixture f(3000);
  assert(f.Start("late-raise"));
  for (uint64_t tick = 40; tick <= 3500; tick += 10) f.Tick(tick);
  assert(f.io.servoWrites == 0);
  // Exercise the presentation owner's own deadline revalidation separately
  // from the earlier common effects stall guard. Admission must precede every
  // output of the denied motion phase; fault cleanup may write a dark frame.
  LightWrites raiseLights{&f.io};
  f.io.lightsCallback = LightWrites::Observe; f.io.lightsContext = &raiseLights;
  f.adapter.Tick(9500);
  assert(f.io.servoWrites == 0 && !f.effects.Healthy() && f.journal.pending);
  assert(raiseLights.nonBlackFrames == 0);
  for (uint8_t value : f.io.pixels) assert(value == 0);

  Fixture lower(3000);
  assert(lower.Start("late-lower"));
  for (uint64_t tick = 40; tick <= 7000; tick += 10) lower.Tick(tick);
  assert(lower.adapter.IsRaised(3) && lower.io.servoWrites == 1);
  LightWrites lowerLights{&lower.io};
  lower.io.lightsCallback = LightWrites::Observe; lower.io.lightsContext = &lowerLights;
  lower.adapter.ObserveOwnerTime(14000);
  assert(!lower.adapter.Lower(3));
  assert(lower.io.servoWrites == 1 && !lower.effects.Healthy() && lower.journal.pending);
  assert(lowerLights.nonBlackFrames == 0);
  for (uint8_t value : lower.io.pixels) assert(value == 0);
}

void StopEveryPhaseAndOldCleanup() {
  // Dense boundary sweep includes preparing, both calibrated moves, display,
  // rest and idle. The common owner always processes Stop before new starts.
  for (uint64_t stoppedAt = 30; stoppedAt < 10400; stoppedAt += 50) {
    Fixture f;
    assert(f.Start("stop-sweep"));
    for (uint64_t tick = 40; tick < stoppedAt; tick += 10) f.Tick(tick);
    SelectionStep step;
    f.runtime.Stop(step);
    const int servos = f.io.servoWrites, motors = f.io.vibrationWrites;
    PendingProposal pending;
    assert(!f.presentation.Start(pending, f.state, stoppedAt));
    for (uint64_t tick = stoppedAt + 10; tick <= stoppedAt + 300; tick += 10) f.Tick(tick);
    assert(f.io.servoWrites == servos && f.io.vibrationWrites == motors && !f.io.run);
  }
  Fixture f;
  assert(f.Start("old-operation"));
  const uint64_t oldGeneration = f.adapter.Diagnostics().generation;
  for (uint64_t tick = 40; tick < 12000; tick += 10) f.Tick(tick);
  assert(f.Start("new-operation", 12000));
  assert(std::string(f.adapter.Diagnostics().sceneHash) == PresentationScore::ShortSceneHash);
  assert(std::string(f.adapter.Diagnostics().variant) == "short");
  const int lights = f.io.lightWrites;
  assert(!f.adapter.CancelOperation("old-operation", oldGeneration));
  assert(f.io.lightWrites == lights && std::string(f.adapter.Diagnostics().state) == "running");
}


SceneIntentContext Context(const Fixture& f) {
  SceneIntentContext context;
  context.bootId = "synthetic-host"; context.sessionId = "session";
  context.responseId = "response"; context.sessionEpoch = 1;
  context.responseGeneration = 2; context.phaseRevision = 3;
  context.deviceGeneration = f.runtime.Generation(); context.validUntilDeviceMs = 1500;
  return context;
}
SceneIntent Intent(SceneIntentKind kind, const std::string& id) {
  SceneIntent intent;
  intent.intentId = id; intent.bootId = "synthetic-host"; intent.sessionId = "session";
  intent.responseId = "response"; intent.sessionEpoch = 1;
  intent.responseGeneration = 2; intent.phaseRevision = 3;
  intent.kind = kind; intent.issuedAtDeviceMs = 30; intent.validUntilDeviceMs = 1200;
  return intent;
}
void IntentScopesAndAdmission() {
  Fixture f;
  SceneIntentBridge bridge(f.adapter);
  auto context = Context(f);
  bridge.SetContext(context);
  auto wake = Intent(SceneIntentKind::Wake, "wake-1");
  assert(bridge.Accept(wake, 30) == SceneIntentResult::Started);
  assert(std::string(f.adapter.Diagnostics().sceneId) == "wake");
  for (const auto& accent : PresentationScore::Accents) {
    if (std::string(accent.sceneId) == "wake") {
      assert(std::string(f.adapter.Diagnostics().sceneHash) == accent.sceneHash);
      assert(std::string(f.adapter.Diagnostics().variant) == accent.variant);
    }
  }
  assert(f.runtime.HasDecoration() && !f.runtime.AcquireManualControl("lease", 30));
  assert(bridge.Accept(wake, 31) == SceneIntentResult::Duplicate);
  auto changed = wake; changed.validUntilDeviceMs = 1300;
  assert(bridge.Accept(changed, 31) == SceneIntentResult::Conflict);
  bridge.CancelResponse("old-response", 2);
  assert(f.runtime.HasDecoration());
  bridge.CancelResponse("response", 2);
  assert(!f.runtime.HasDecoration());
  assert(bridge.Accept(wake, 40) == SceneIntentResult::Expired);
  assert(f.io.servoWrites == 0 && f.io.vibrationWrites == 0 && f.journal.begins == 0);
  context.responseId = "response-2"; ++context.responseGeneration;
  bridge.SetContext(context);
  auto stale = Intent(SceneIntentKind::Consider, "stale");
  assert(bridge.Accept(stale, 40) == SceneIntentResult::Stale);
  stale.responseId = context.responseId; stale.responseGeneration = context.responseGeneration;
  assert(bridge.Accept(stale, 40) == SceneIntentResult::Started);
  for (uint64_t t = 50; t < 400; t += 10) f.Tick(t);
  assert(!f.runtime.HasDecoration() && std::string(f.adapter.Diagnostics().state) == "completed");
  assert(f.adapter.Diagnostics().reduced); // Optional consider tone is unsupported by this LED-only port.

  Fixture stoppedAccent;
  assert(stoppedAccent.adapter.StartAccent("wake", "accent-stop", stoppedAccent.runtime.Generation(), 1000, 30));
  SelectionStep stop;
  stoppedAccent.runtime.Stop(stop);
  stoppedAccent.Tick(40);
  assert(!stoppedAccent.runtime.HasDecoration());
  for (uint8_t value : stoppedAccent.io.pixels) assert(value == 0);

  Fixture choice;
  SceneIntentBridge choiceBridge(choice.adapter);
  context = Context(choice); context.decisionId = "decision"; context.selectionOperationId = "selection";
  choiceBridge.SetContext(context);
  auto intent = Intent(SceneIntentKind::ChoiceAccent, "choice"); intent.decisionId = "decision";
  assert(choiceBridge.Accept(intent, 30) == SceneIntentResult::AttachedToSelection);
  assert(choiceBridge.Accept(intent, 30) == SceneIntentResult::Duplicate);
  assert(choice.io.servoWrites == 0 && choice.journal.begins == 0);
  assert(choice.Start("selection"));
  choiceBridge.CancelResponse("response", 2);
  for (uint64_t t = 40; t < 12000; t += 10) choice.Tick(t);
  assert(choice.io.servoWrites == 2 && choice.journal.restores == 1);

  Fixture busy;
  busy.runtime.SetExternalActivity(true);
  assert(!busy.adapter.StartAccent("wake", "intent", busy.runtime.Generation(), 1000, 30));
  assert(!busy.adapter.StartAccent("special-referral", "intent", busy.runtime.Generation(), 1000, 30));
}

void CalibrationAndIdleCapabilities() {
  Fixture f;
  const int startupLights = f.io.lightWrites;
  for (uint64_t tick = 40; tick < 1000; tick += 10) f.Tick(tick);
  assert(f.io.lightWrites == startupLights && f.io.servoWrites == 0 && f.io.vibrationWrites == 0);
  assert(f.Start("calibration-snapshot", 1000));
  const auto original = f.adapter.Diagnostics();
  assert(f.effects.Configure(f.calibration, 2));
  f.Tick(1010);
  assert(std::string(f.adapter.Diagnostics().state) == "fault");
  assert(f.adapter.Diagnostics().calibrationId == original.calibrationId && f.journal.pending);

  Fixture busy;
  SceneIntentBridge bridge(busy.adapter);
  auto context = Context(busy);
  bridge.SetContext(context);
  busy.runtime.SetExternalActivity(true);
  auto intent = Intent(SceneIntentKind::Wake, "busy-no-queue");
  assert(bridge.Accept(intent, 30) == SceneIntentResult::BusyOrUnsupported);
  busy.runtime.SetExternalActivity(false);
  // Re-delivery is deduped rather than a delayed implicit retry.
  assert(bridge.Accept(intent, 40) == SceneIntentResult::Duplicate);
  for (uint64_t tick = 40; tick < 400; tick += 10) busy.Tick(tick);
  assert(!busy.runtime.HasDecoration() && busy.io.lightWrites == 0);
}

int main() {
  JournalLatencyUsesActualOwnerClock();
  AbsoluteClockAndNoBurst(); SnapshotStopAndRing(); LateMotionNeverStarts();
  StopEveryPhaseAndOldCleanup(); IntentScopesAndAdmission(); CalibrationAndIdleCapabilities();
}

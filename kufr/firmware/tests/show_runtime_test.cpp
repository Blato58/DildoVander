#include "physical_effects_test_support.h"
#include "../src/features/scene_intent/SceneIntentBridge.h"
#include <cstring>
#include <type_traits>

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
  Fixture f(3000);
  assert(f.Start("late-raise"));
  for (uint64_t tick = 40; tick <= 3500; tick += 10) f.Tick(tick);
  assert(f.io.servoWrites == 0);
  // Exercise the presentation owner's own deadline revalidation separately
  // from the earlier common effects stall guard. No late lift can escape it.
  f.adapter.Tick(9500);
  assert(f.io.servoWrites == 0 && !f.effects.Healthy() && f.journal.pending);
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
  AbsoluteClockAndNoBurst(); SnapshotStopAndRing(); LateMotionNeverStarts();
  StopEveryPhaseAndOldCleanup(); IntentScopesAndAdmission(); CalibrationAndIdleCapabilities();
}

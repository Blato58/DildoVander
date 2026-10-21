#include <cassert>
#include <iostream>
#include <vector>
#include "../src/features/physical_effects/PhysicalPresentation.h"

#include "physical_effects_test_support.h"

void PassiveHealthKeepsInspectionAndFaultsSeparate() {
  FakeIo io;
  PhysicalEffects effects(io);
  assert(!effects.PassiveHealthy() && !effects.Healthy());
  std::array<ServoCalibration, 6> calibration;
  calibration.fill({1100, 1900, 500});
  assert(effects.Configure(calibration, 1));
  assert(!effects.PassiveHealthy());  // No successful input observation yet.
  effects.Tick(0);
  assert(effects.PassiveHealthy() && !effects.Healthy());
  assert(!effects.Move(1, true) && !effects.StartVibration(1));
  assert(!io.run && io.servoWrites == 0 && io.vibrationWrites == 0);

  effects.Tick(250);  // A control-loop failure is real health evidence.
  assert(!effects.PassiveHealthy() && !effects.Healthy());
  effects.Tick(260);
  assert(!effects.PassiveHealthy());  // A good input sample cannot clear a fault.
  assert(effects.RecoverAfterInspection());
  assert(effects.PassiveHealthy() && effects.Healthy() && !io.run);

  assert(effects.Configure(calibration, 2));
  assert(effects.PassiveHealthy() && !effects.Healthy());
  assert(!effects.Move(1, true) && !effects.StartVibration(1));
  io.fail = true;
  effects.Disable();
  io.fail = false;
  effects.Tick(270);
  assert(!effects.PassiveHealthy() && !effects.Healthy() && !io.run);
}

void LimitsAndFaults() {
  Fixture f;
  assert(!f.effects.Move(0, true) && !f.effects.StartVibration(7));
  assert(f.effects.Highlight(2, 255, 255, 255));
  unsigned lit = 0;
  for (unsigned i = 0; i < 108; i += 3) {
    if (f.io.pixels[i]) ++lit;
    assert(f.io.pixels[i] <= 64 && f.io.pixels[i + 1] <= 64 && f.io.pixels[i + 2] <= 64);
  }
  assert(lit == 6 && !f.effects.Highlight(7, 255, 255, 255));
  assert(f.effects.StartVibration(6) && f.io.run && f.io.channel == 11);
  assert(!f.effects.Move(2, true) && !f.effects.StartVibration(1));
  f.effects.Tick(180);
  assert(!f.io.run && f.effects.Healthy());
  assert(f.effects.Move(2, true) && f.io.channel == 1 && f.io.pulse == 1900);
  for (uint64_t t = 190; t <= 680; t += 10) f.effects.Tick(t);
  assert(!f.io.run && f.effects.IsRaised(2)); // Commanded position after calibrated travel.
  f.effects.Fault();
  assert(f.effects.RecoverAfterInspection());
  assert(f.effects.StartVibration(1));
  f.effects.Tick(930); // A 250ms owner stall faults before the next output.
  assert(!f.effects.Healthy() && !f.io.run);
  f.effects.Tick(940);
  assert(f.effects.RecoverAfterInspection());
  f.io.fail = true;
  f.effects.Tick(970);
  assert(!f.io.run && !f.effects.Healthy());

  Fixture invalid;
  invalid.calibration[0].travelMs = 3001;
  assert(!invalid.effects.Configure(invalid.calibration, 2));
  assert(invalid.effects.CalibrationRevision() == 0 && !invalid.io.run);
}

void SingleLedSegmentBoundary() {
  Fixture f;
  for (unsigned mask = 0; mask <= 255; ++mask) {
    const bool allowed = mask <= 63 && (mask & (mask - 1)) == 0;
    const auto previousFrame = f.io.pixels;
    const int writes = f.io.lightWrites;
    assert(f.effects.Illuminate(static_cast<uint8_t>(mask), 255, 255, 255) == allowed);
    assert(f.effects.Healthy());
    if (!allowed) {
      assert(f.io.lightWrites == writes && f.io.pixels == previousFrame);
    } else {
      assert(f.io.lightWrites == writes + 1);
      for (unsigned compartment = 0; compartment < 6; ++compartment) {
        for (unsigned i = compartment * 18; i < (compartment + 1) * 18; ++i) {
          assert(f.io.pixels[i] == ((mask & (1u << compartment)) ? 64 : 0));
        }
      }
    }
  }
  assert(f.effects.Highlight(0, 0, 0, 0));
  for (auto value : f.io.pixels) assert(value == 0);
}

class CueSink final : public PresentationAudioPort {
 public:
  std::vector<uint16_t> notes;
  unsigned stops = 0;
  void Cue(uint16_t hz, uint16_t ms) override { assert(ms <= 200); notes.push_back(hz); }
  void StopCues() override { ++stops; }
};

const PresentationScore::Step& MainPhase(PresentationScore::Stage stage) {
  for (const auto& step : PresentationScore::Main) {
    if (step.stage == stage) return step;
  }
  assert(false);
  return PresentationScore::Main[0];
}

void AssertSelectedLights(const FakeIo& io, const PresentationScore::Step& step) {
  // Fixture selects compartment 3; all other compartments must be dark.
  for (unsigned i = 0; i < 108; i += 3) {
    const bool selected = i >= 36 && i < 54;
    assert(io.pixels[i] == (selected ? step.green * KufrBoard::InitialBrightness / 255 : 0));
    assert(io.pixels[i + 1] == (selected ? step.red * KufrBoard::InitialBrightness / 255 : 0));
    assert(io.pixels[i + 2] == (selected ? step.blue * KufrBoard::InitialBrightness / 255 : 0));
  }
}

void EveryAuthoredPhaseAndRestCompletion() {
  const auto& raise = MainPhase(PresentationScore::Stage::Raise);
  const auto& show = MainPhase(PresentationScore::Stage::Show);
  const auto& lower = MainPhase(PresentationScore::Stage::Lower);
  const auto& rest = MainPhase(PresentationScore::Stage::Rest);
  Fixture f(20); // Hardware motion finishes before the illustrative viewer travel.
  CueSink audio;
  f.adapter.BindAudio(audio);
  assert(f.Start("all-authored-phases"));
  bool sawRaise = false, sawShow = false, sawLower = false;
  uint64_t restAt = 0;
  for (uint64_t t = 40; t < 15000; t += 10) {
    f.Tick(t);
    if (f.io.servoWrites == 1 && f.io.run) {
      sawRaise = true;
      AssertSelectedLights(f.io, raise);
      if (raise.toneHz) assert(audio.notes.back() == raise.toneHz);
    }
    if (f.presentation.State() == WandPresentationState::Showing) {
      sawShow = true;
      AssertSelectedLights(f.io, show);
      if (show.toneHz) assert(audio.notes.back() == show.toneHz);
    }
    if (f.io.servoWrites == 2 && f.io.run) {
      sawLower = true;
      AssertSelectedLights(f.io, lower);
      if (lower.toneHz) assert(audio.notes.back() == lower.toneHz);
    }
    if (f.io.servoWrites == 2 && f.effects.IsResting(3) && !restAt) restAt = t;
    if (restAt && t - restAt < rest.durationMs) {
      assert(!f.io.run && f.journal.pending && f.journal.restores == 0);
      assert(f.presentation.State() == WandPresentationState::Lowering);
      assert(!f.adapter.IsResting(3));
      AssertSelectedLights(f.io, rest);
      assert(audio.stops == 1);
    }
    if (f.presentation.State() == WandPresentationState::Idle) {
      assert(restAt && t - restAt >= rest.durationMs);
      assert(!f.journal.pending && f.journal.restores == 1 && !f.io.run);
      break;
    }
  }
  assert(sawRaise && sawShow && sawLower && restAt);
  assert(f.presentation.State() == WandPresentationState::Idle);
  assert(f.adapter.LowerTimeoutMs(3) == f.effects.MotionTimeoutMs(3) + rest.durationMs);
}

void SharedScoreAndRepeat() {
  Fixture f;
  CueSink audio;
  f.adapter.BindAudio(audio);
  assert(f.Start("main-score"));
  unsigned lit = 0;
  for (unsigned i = 0; i < 108; i += 3) if (f.io.pixels[i]) ++lit;
  assert(lit == 6 && f.io.servoWrites == 0);
  AssertSelectedLights(f.io, PresentationScore::Main[0]);
  for (uint64_t t = 40; t < 12000; t += 10) f.Tick(t);
  assert(f.presentation.State() == WandPresentationState::Idle);
  assert(audio.notes.front() == 220 && audio.stops == 1);
  assert(f.io.vibrationWrites == 3 && f.io.servoWrites == 2 && !f.io.run);
  assert(f.Start("short-score",12000));
  for (uint64_t t = 12010; t < 20000; t += 10) f.Tick(t);
  assert(f.presentation.State() == WandPresentationState::Idle);
  assert(f.io.vibrationWrites == 4 && f.io.servoWrites == 4 && !f.io.run);
  assert(f.Start("stop-score",20000));
  SelectionStep stopped;
  f.runtime.Stop(stopped);
  assert(!f.io.run && audio.stops >= 3);
}

void AutonomousCycle() {
  Fixture f;
  assert(f.Start("proposal-1"));
  assert(f.journal.pending && f.io.servoWrites == 0 && f.io.vibrationWrites == 0);
  for (uint64_t t = 40; t < 12000; t += 10) {
    f.Tick(t);
  }
  assert(f.presentation.State() == WandPresentationState::Idle);
  assert(f.journal.begins == 1 && f.journal.restores == 1 && !f.journal.pending);
  assert(f.io.vibrationWrites == 3 && f.io.servoWrites == 2 && !f.io.run);
  ConversationRuntimeSnapshot restored;
  assert(f.state.Read(restored) && restored.readyMask == 63);
  assert(!f.Start("proposal-1", 12000)); // A replay cannot move again.

  Fixture failed;
  assert(failed.Start("owner-stall"));
  for (uint64_t t = 40; t < 2000; t += 10) failed.Tick(t);
  failed.Tick(2250);
  assert(failed.presentation.State() == WandPresentationState::Fault);
  assert(failed.journal.pending && failed.journal.restores == 0 && !failed.io.run);

  Fixture stop;
  assert(stop.Start("stop"));
  for (uint64_t t = 40; t < 2030; t += 10) { stop.Tick(t); }
  SelectionStep step;
  stop.runtime.Stop(step);
  const int writes = stop.io.servoWrites;
  for (uint64_t t = 2030; t < 9000; t += 10) stop.Tick(t);
  assert(stop.io.servoWrites == writes && !stop.io.run && stop.journal.pending);
  assert(stop.presentation.State() == WandPresentationState::Fault);

  Fixture journalFailure;
  journalFailure.journal.fail = true;
  assert(!journalFailure.Start("unpersisted"));
  assert(journalFailure.io.servoWrites == 0 && journalFailure.io.vibrationWrites == 0);
}

void ManualCycle() {
  Fixture f;
  assert(f.runtime.AcquireManualControl("lease", 30));
  SelectionCommand command;
  command.commandId = "manual"; command.deviceId = "device"; command.bootId = "boot";
  command.controlLeaseId = "lease"; command.sequence = 1;
  command.expectedRevision = f.control.Revision(); command.issuedAtDeviceMs = 30;
  command.maxAgeMs = 2000; command.compartment = 2;
  assert(f.runtime.AcceptSelection(command, true, 30).outcome == CommandOutcome::Accepted);
  SelectionStep step;
  assert(f.runtime.BeginAcceptedSelection(30, step));
  for (uint64_t t = 40; t < 12000; t += 10) {
    assert(f.runtime.RenewManualLease("lease", t));
    f.Tick(t);
  }
  assert(!f.sequence.IsBusy() && !f.journal.pending && f.journal.restores == 1);
  assert(f.control.ReadyMask() == 63 && f.io.servoWrites == 2 && f.io.vibrationWrites == 3);
}

void InterruptedReturn() {
  // Cover both new manual phases: holding a raised prop and returning it.
  for (uint64_t lostAt : {uint64_t{2500}, uint64_t{7500}}) {
    Fixture f;
    assert(f.runtime.AcquireManualControl("lease", 30));
    SelectionCommand command;
    command.commandId = "interrupted"; command.deviceId = "device"; command.bootId = "boot";
    command.controlLeaseId = "lease"; command.sequence = 1;
    command.expectedRevision = f.control.Revision(); command.issuedAtDeviceMs = 30;
    command.maxAgeMs = 2000; command.compartment = 2;
    assert(f.runtime.AcceptSelection(command, true, 30).outcome == CommandOutcome::Accepted);
    SelectionStep step;
    assert(f.runtime.BeginAcceptedSelection(30, step));
    for (uint64_t t = 40; t <= lostAt; t += 10) {
      assert(f.runtime.RenewManualLease("lease", t));
      f.Tick(t);
    }
    const int writes = f.io.servoWrites;
    assert(writes == (lostAt == 2500 ? 1 : 2));
    assert(f.runtime.DisconnectManualControl("lease", step));
    for (uint64_t t = lostAt + 10; t < lostAt + 1000; t += 10) f.Tick(t);
    assert(!f.io.run && !f.sequence.IsBusy() && f.sequence.NeedsRecovery());
    assert(f.io.servoWrites == writes && f.journal.pending && f.journal.restores == 0);
  }

  Fixture f;
  assert(f.Start("return-journal-failure"));
  for (uint64_t t = 40; t < 12000; t += 10) {
    if (f.io.servoWrites == 2) f.journal.fail = true;
    f.Tick(t);
  }
  assert(f.io.servoWrites == 2 && !f.io.run && f.journal.pending && f.journal.restores == 0);
  assert(f.presentation.State() == WandPresentationState::Fault);
}

void LongerCalibratedTravel() {
  Fixture f(1500);
  assert(f.effects.MotionTimeoutMs(3) == 1750);
  assert(f.Start("longer-than-500ms"));
  for (uint64_t t = 40; t < 15000; t += 10) f.Tick(t);
  assert(f.presentation.State() == WandPresentationState::Idle);
  assert(f.journal.restores == 1 && f.io.servoWrites == 2 && !f.io.run);

  Fixture manual(1500);
  SelectionStep initial;
  assert(manual.sequence.Start(2, 30, SelectionControlMode::Manual, initial));
  for (uint64_t t = 40; t < 15000; t += 10) {
    manual.effects.Tick(t);
    SelectionStep step;
    manual.sequence.ObserveControlLoopPass(t, step);
    manual.sequence.Update(t, step);
  }
  assert(!manual.sequence.IsBusy() && !manual.sequence.NeedsRecovery());
  assert(manual.io.servoWrites == 2 && manual.effects.IsResting(2));
}

void MaximumTravelStillHonorsAbsoluteDeadline() {
  Fixture f(3000);
  assert(f.Start("maximum-travel-deadline"));
  for (uint64_t t = 130; t <= 14930; t += 100) f.Tick(t);
  // A 100ms loop cannot promise the nominal 10ms phase handovers at maximum
  // calibration. It must fail closed before the remaining lifecycle won't fit.
  for (uint64_t t = 15030; t <= 15130; t += 100) f.Tick(t);
  assert(f.presentation.State() == WandPresentationState::Fault);
  assert(!f.io.run && f.journal.pending && f.journal.restores == 0);

  Fixture nominal(3000);
  assert(nominal.Start("nominal-maximum-travel"));
  for (uint64_t t = 40; t <= 15030; t += 10) nominal.Tick(t);
  assert(nominal.presentation.State() == WandPresentationState::Idle);
  assert(nominal.io.servoWrites == 2 && nominal.journal.restores == 1);

  Fixture stalled(3000);
  assert(stalled.Start("hard-stall"));
  stalled.Tick(30 + WandPresentation::MaximumDurationMs);
  assert(stalled.presentation.State() == WandPresentationState::Fault);
  assert(!stalled.io.run && !stalled.effects.Healthy());
  assert(stalled.journal.pending && stalled.journal.restores == 0);
  const int writes = stalled.io.servoWrites;
  stalled.Tick(15130);
  assert(stalled.io.servoWrites == writes && stalled.journal.pending);
}

void DurableCompletionWaitRetainsTheFirstCloseClock() {
  for (uint64_t closeDelay : {uint64_t(249), uint64_t(250)}) {
    Fixture f;
    uint64_t clock = 30;
    f.adapter.BindClock([](void* p) { return *static_cast<uint64_t*>(p); }, &clock);
    f.journal.holdClose = true;
    f.journal.holdProof = true;
    assert(f.Start("delayed-close", clock));
    for (clock = 40; clock < 14000; clock += 10) {
      f.Tick(clock);
      if (f.presentation.State() == WandPresentationState::Completing) break;
    }
    assert(f.presentation.State() == WandPresentationState::Completing);
    const uint64_t closeStarted = clock;
    const int servoWrites = f.io.servoWrites, vibrationWrites = f.io.vibrationWrites;
    assert(!f.io.run && f.journal.pending && f.journal.restores == 0);
    assert(!f.runtime.HasValidatedAutonomousCompletion());
    // Every poll keeps the ORIGINAL first close time. Repeated calls cannot
    // renew the 250 ms physical completion window.
    for (++clock; clock < closeStarted + closeDelay; ++clock) f.Tick(clock);
    f.journal.holdClose = false;
    f.Tick(clock);
    assert(f.journal.restores == 1 && !f.journal.pending);
    if (closeDelay == 250) {
      assert(f.presentation.State() == WandPresentationState::Fault);
      assert(!f.runtime.HasValidatedAutonomousCompletion());
      assert(f.journal.completedId.empty() && !f.effects.Healthy());
      continue;
    }
    assert(f.presentation.State() == WandPresentationState::Completing);
    assert(f.runtime.HasValidatedAutonomousCompletion());
    assert(f.journal.completedId.empty());
    // Already-validated proof bookkeeping can acknowledge after the physical
    // deadline, with all outputs disabled and no newly sampled timing proof.
    for (++clock; clock < 20000; ++clock) f.Tick(clock);
    f.Tick(clock);
    assert(f.presentation.State() == WandPresentationState::Completing);
    assert(f.runtime.HasValidatedAutonomousCompletion() && !f.io.run);
    assert(f.io.servoWrites == servoWrites && f.io.vibrationWrites == vibrationWrites);
    f.journal.holdProof = false;
    f.Tick(++clock);
    assert(f.presentation.State() == WandPresentationState::Idle);
    assert(f.journal.completedId == "delayed-close");
    assert(f.effects.Healthy() && !f.runtime.HasValidatedAutonomousCompletion());
  }
}

void StopWhileCompletionWaitsNeverReportsSuccess() {
  for (bool sealFirst : {false, true}) {
    Fixture f;
    f.journal.holdClose = !sealFirst;
    f.journal.holdProof = true;
    assert(f.Start("stop-closing"));
    uint64_t at = 40;
    for (; at < 14000; at += 10) {
      f.Tick(at);
      if (f.presentation.State() == WandPresentationState::Completing) break;
    }
    assert(f.presentation.State() == WandPresentationState::Completing);
    assert(f.runtime.HasValidatedAutonomousCompletion() == sealFirst);
    const int writes = f.io.servoWrites;
    SelectionStep step;
    f.runtime.Stop(step);
    assert(f.presentation.State() == WandPresentationState::Fault);
    assert(!f.runtime.HasValidatedAutonomousCompletion() && !f.io.run);
    f.journal.holdClose = false;
    f.journal.holdProof = false;
    f.Tick(at + 10);
    assert(f.presentation.State() == WandPresentationState::Fault);
    assert(f.journal.completedId.empty() && f.io.servoWrites == writes);
  }
}

int main() {
  DurableCompletionWaitRetainsTheFirstCloseClock();
  StopWhileCompletionWaitsNeverReportsSuccess();
  PassiveHealthKeepsInspectionAndFaultsSeparate(); LimitsAndFaults(); SingleLedSegmentBoundary(); EveryAuthoredPhaseAndRestCompletion(); SharedScoreAndRepeat(); AutonomousCycle(); ManualCycle(); InterruptedReturn(); LongerCalibratedTravel(); MaximumTravelStillHonorsAbsoluteDeadline();
  std::cout << "Physical limits, calibrated commanded travel, journal, autonomous/manual return and Stop passed\n";
}

#include <cassert>
#include <iostream>
#include <vector>
#include "../src/features/physical_effects/PhysicalPresentation.h"

class FakeIo final : public PhysicalIo {
 public:
  PhysicalInputs inputs{false};
  bool run = false, fail = false;
  int servoWrites = 0, vibrationWrites = 0, clears = 0;
  uint8_t channel = 0;
  uint16_t pulse = 0;
  std::array<uint8_t, 108> pixels{};
  void EnableOutputs(bool enabled) override { run = enabled; }
  bool FullOff() override { assert(!run); ++clears; return !fail; }
  bool Servo(uint8_t selected, uint16_t us) override {
    assert(!run); channel = selected; pulse = us; ++servoWrites; return !fail;
  }
  bool Vibration(uint8_t selected, bool enabled) override {
    assert(!run && enabled); channel = selected; ++vibrationWrites; return !fail;
  }
  bool Lights(const std::array<uint8_t, 108>& frame) override { pixels = frame; return !fail; }
  bool ReadInputs(PhysicalInputs& value) override { value = inputs; return !fail; }
};

class Journal final : public CommandJournal {
 public:
  bool pending = false, fail = false;
  uint8_t selected = 0, mask = 63;
  int begins = 0, restores = 0;
  bool HasPending() const override { return pending; }
  uint8_t PendingCompartment() const override { return selected; }
  uint8_t ReadyMask() const override { return mask; }
  bool Begin(const std::string&, uint8_t compartment) override {
    if (fail) return false;
    ++begins; pending = true; selected = compartment; return true;
  }
  bool Resolve(uint8_t value) override { mask = value; pending = false; return !fail; }
  bool ResolveRestored(uint8_t value) override {
    if (fail || !pending || value != selected) return false;
    ++restores; pending = false; selected = 0; return true;
  }
  bool PersistReadyMask(uint8_t value) override { mask = value; return !fail; }
};

class SafetyAdapter final : public SafetyPort {
 public:
  explicit SafetyAdapter(PhysicalEffects& effects) : effects_(effects) {}
  void DisableActuators() override { effects_.Disable(); }
  void StartVibration(uint8_t n) override { if (!effects_.StartVibration(n)) effects_.Fault(); }
  void ReleaseLatch(uint8_t n) override { if (!effects_.Move(n, true)) effects_.Fault(); }
  void LowerProp(uint8_t n) override { if (!effects_.Move(n, false)) effects_.Fault(); }
  uint32_t MotionTimeoutMs(uint8_t n) const override { return effects_.MotionTimeoutMs(n); }
  bool PropRaised(uint8_t n) const override { return effects_.IsRaised(n); }
  bool PropResting(uint8_t n) const override { return effects_.IsResting(n); }
 private:
  PhysicalEffects& effects_;
};

struct Fixture {
  FakeIo io;
  PhysicalEffects effects{io};
  SafetyAdapter port{effects};
  SafetyController safety{port};
  SelectionSequence sequence{safety, true};
  Journal journal;
  DeviceControl control{"device", "boot", sequence, journal, 1};
  ConversationRuntimeState state{false, 0, ""};
  DeviceControlRuntime runtime{control, sequence, state, "catalog"};
  PhysicalPresentation adapter{effects, runtime};
  WandPresentation presentation{adapter};
  std::array<ServoCalibration, 6> calibration{};
  explicit Fixture(uint16_t travelMs = 500) {
    calibration.fill({1100, 1900, travelMs});
    assert(effects.Configure(calibration, 1));
    effects.Tick(0); effects.Tick(30);
    assert(effects.RecoverAfterInspection());
    assert(runtime.BindPresentation(presentation));
    assert(runtime.RecoverAfterInspection());
  }
  void Tick(uint64_t now) {
    effects.Tick(now);
    SelectionStep step;
    runtime.Tick(now, step);
    adapter.Tick(now);
  }
  bool Start(const std::string& id, uint64_t now = 30) {
    ConversationRuntimeSnapshot snapshot;
    assert(state.Read(snapshot));
    TurnProposal proposal;
    proposal.proposalId = id;
    proposal.compartment = 3;
    proposal.catalogVersion = snapshot.catalogVersion;
    proposal.generation = snapshot.generation;
    proposal.deviceRevision = snapshot.deviceRevision;
    PendingProposal pending;
    assert(pending.Store(proposal, snapshot, state, now, 30000));
    adapter.Tick(now);
    return presentation.Start(pending, state, now);
  }

};

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
  assert(lit == 36 && f.io.servoWrites == 0);
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

int main() {
  LimitsAndFaults(); EveryAuthoredPhaseAndRestCompletion(); SharedScoreAndRepeat(); AutonomousCycle(); ManualCycle(); InterruptedReturn(); LongerCalibratedTravel();
  std::cout << "Physical limits, calibrated commanded travel, journal, autonomous/manual return and Stop passed\n";
}

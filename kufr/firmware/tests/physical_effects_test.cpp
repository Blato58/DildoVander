#include <cassert>
#include <iostream>
#include <vector>
#include "../src/features/physical_effects/PhysicalPresentation.h"

class FakeIo final : public PhysicalIo {
 public:
  PhysicalInputs inputs{true, true, false, 63, 0};
  bool run = false, fail = false;
  int servoWrites = 0, vibrationWrites = 0, clears = 0, heartbeats = 0;
  uint8_t channel = 0;
  uint16_t pulse = 0;
  std::array<uint8_t, 108> pixels{};
  void RunRequest(bool enabled) override { run = enabled; }
  bool FullOff() override { assert(!run); ++clears; return !fail; }
  bool Servo(uint8_t selected, uint16_t us) override {
    assert(!run); channel = selected; pulse = us; ++servoWrites; return !fail;
  }
  bool Vibration(uint8_t selected, bool enabled) override {
    assert(!run && enabled); channel = selected; ++vibrationWrites; return !fail;
  }
  bool Lights(const std::array<uint8_t, 108>& frame) override { pixels = frame; return !fail; }
  bool ReadInputs(PhysicalInputs& value) override { value = inputs; return !fail; }
  void Heartbeat(uint64_t) override { ++heartbeats; }
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
  Fixture() {
    calibration.fill({1100, 1900, 500});
    assert(effects.Configure(calibration, 1, true));
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
  void Feedback() {
    if (!io.run || io.pulse == 0) return;
    const uint8_t bit = static_cast<uint8_t>(1u << io.channel);
    if (io.pulse == 1900) { io.inputs.restingMask &= ~bit; io.inputs.raisedMask |= bit; }
    else { io.inputs.raisedMask &= ~bit; io.inputs.restingMask |= bit; }
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
  assert(!f.io.run && !f.effects.Healthy()); // No endpoint; no timed success.
  assert(f.effects.RecoverAfterInspection());
  assert(f.effects.StartVibration(1));
  f.effects.Tick(930); // A 250ms owner stall faults before the next output.
  assert(!f.effects.Healthy() && !f.io.run);
  f.effects.Tick(940);
  assert(f.effects.RecoverAfterInspection());
  f.io.inputs.enabled = false;
  f.effects.Tick(950);
  assert(!f.effects.Healthy() && !f.effects.RecoverAfterInspection());
  f.io.inputs.enabled = true;
  f.io.inputs.raisedMask = 1; // Contradictory contacts fail closed.
  f.effects.Tick(960);
  assert(!f.effects.Healthy());
  f.io.inputs.raisedMask = 0;
  f.io.fail = true;
  f.effects.Tick(970);
  assert(!f.io.run && !f.effects.Healthy());

  Fixture invalid;
  invalid.calibration[0].travelMs = 501;
  assert(!invalid.effects.Configure(invalid.calibration, 2, true));
  assert(invalid.effects.CalibrationRevision() == 0 && !invalid.io.run);
}

void AutonomousCycle() {
  Fixture f;
  assert(f.Start("proposal-1"));
  assert(f.journal.pending && f.io.servoWrites == 0 && f.io.vibrationWrites == 0);
  for (uint64_t t = 40; t < 9500; t += 10) {
    f.Feedback();
    f.Tick(t);
  }
  assert(f.presentation.State() == WandPresentationState::Idle);
  assert(f.journal.begins == 1 && f.journal.restores == 1 && !f.journal.pending);
  assert(f.io.vibrationWrites == 3 && f.io.servoWrites == 2 && !f.io.run);
  ConversationRuntimeSnapshot restored;
  assert(f.state.Read(restored) && restored.readyMask == 63);
  assert(!f.Start("proposal-1", 9500)); // A replay cannot move again.

  Fixture failed;
  assert(failed.Start("blocked"));
  for (uint64_t t = 40; t < 3500; t += 10) failed.Tick(t); // No servo feedback.
  assert(failed.presentation.State() == WandPresentationState::Fault);
  assert(failed.journal.pending && failed.journal.restores == 0 && !failed.io.run);

  Fixture stop;
  assert(stop.Start("stop"));
  for (uint64_t t = 40; t < 2030; t += 10) { stop.Feedback(); stop.Tick(t); }
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
  for (uint64_t t = 40; t < 9500; t += 10) {
    assert(f.runtime.RenewManualLease("lease", t));
    f.Feedback(); f.Tick(t);
  }
  assert(!f.sequence.IsBusy() && !f.journal.pending && f.journal.restores == 1);
  assert(f.control.ReadyMask() == 63 && f.io.servoWrites == 2 && f.io.vibrationWrites == 3);
}

void InterruptedReturn() {
  // Cover both new manual phases: holding a raised prop and returning it.
  for (uint64_t lostAt : {uint64_t{2500}, uint64_t{7050}}) {
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
      // Confirm raise only; keep the lower move pending at the second cut.
      if (f.io.pulse == 1900) f.Feedback();
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
  for (uint64_t t = 40; t < 9500; t += 10) {
    f.Feedback();
    if (f.io.servoWrites == 2) f.journal.fail = true;
    f.Tick(t);
  }
  assert(f.io.servoWrites == 2 && !f.io.run && f.journal.pending && f.journal.restores == 0);
  assert(f.presentation.State() == WandPresentationState::Fault);
}

int main() {
  LimitsAndFaults(); AutonomousCycle(); ManualCycle(); InterruptedReturn();
  std::cout << "Physical limits, feedback, journal, autonomous/manual return and Stop passed\n";
}

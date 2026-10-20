#include <assert.h>
#include <stdint.h>

#include "../src/features/safety/SafetyController.h"
#include "../src/features/wand_selection/SelectionSequence.h"

namespace {

class FakeSafetyPort : public SafetyPort {
 public:
  void DisableActuators() override {
    ++disableCount;
    vibrationOn = false;
    latchOn = false;
  }

  void StartVibration(uint8_t compartment) override {
    assert(!vibrationOn && !latchOn);
    vibrationOn = true;
    activeCompartment = compartment;
    ++vibrationCount;
  }

  void ReleaseLatch(uint8_t compartment) override {
    assert(!vibrationOn && !latchOn);
    latchOn = true;
    activeCompartment = compartment;
    ++releaseCount;
  }

  bool vibrationOn = false;
  bool latchOn = false;
  uint8_t activeCompartment = 0;
  int disableCount = 0;
  int vibrationCount = 0;
  int releaseCount = 0;
};

struct Fixture {
  Fixture() : safety(port), sequence(safety) { safety.InitializeSafe(); }

  void Recover() {
    assert(sequence.RecoverAfterInspection());
    assert(!sequence.NeedsRecovery());
  }

  FakeSafetyPort port;
  SafetyController safety;
  SelectionSequence sequence;
};

void ExpectStep(SelectionSequence& sequence, uint32_t nowMs,
                SelectionEffect expectedEffect, uint8_t expectedCompartment) {
  SelectionStep step;
  assert(sequence.Update(nowMs, step));
  assert(step.effect == expectedEffect);
  assert(step.compartment == expectedCompartment);
}

void TestCompleteSequence() {
  Fixture fixture;
  SelectionStep step;

  assert(fixture.sequence.NeedsRecovery());
  assert(!fixture.sequence.Start(4, 0, step));
  fixture.Recover();
  assert(!fixture.sequence.Start(0, 0, step));
  assert(!fixture.sequence.Start(7, 0, step));
  assert(fixture.sequence.Start(4, 0, step));
  assert(step.effect == SelectionEffect::SearchStep);
  assert(step.compartment == 1);
  assert(fixture.sequence.IsBusy());
  assert(!fixture.sequence.Start(2, 0, step));
  assert(!fixture.sequence.RecoverAfterInspection());
  assert(!fixture.sequence.Update(99, step));

  for (uint8_t compartment = 2;
       compartment <= SelectionSequence::CompartmentCount; ++compartment) {
    ExpectStep(fixture.sequence, (compartment - 1) * 100,
               SelectionEffect::SearchStep, compartment);
  }

  ExpectStep(fixture.sequence, 600, SelectionEffect::Highlight, 4);
  assert(!fixture.sequence.Update(999, step));
  ExpectStep(fixture.sequence, 1000, SelectionEffect::VibrationOn, 4);
  assert(fixture.port.vibrationOn && !fixture.port.latchOn);
  ExpectStep(fixture.sequence, 1150, SelectionEffect::VibrationOff, 4);
  assert(!fixture.port.vibrationOn);
  ExpectStep(fixture.sequence, 1300, SelectionEffect::VibrationOn, 4);
  ExpectStep(fixture.sequence, 1450, SelectionEffect::VibrationOff, 4);
  ExpectStep(fixture.sequence, 1600, SelectionEffect::VibrationOn, 4);
  ExpectStep(fixture.sequence, 1750, SelectionEffect::VibrationOff, 4);
  ExpectStep(fixture.sequence, 1900, SelectionEffect::ReleaseLatch, 4);
  assert(fixture.port.latchOn && !fixture.port.vibrationOn);
  assert(fixture.port.releaseCount == 1);
  assert(!fixture.sequence.Update(2299, step));
  ExpectStep(fixture.sequence, 2300, SelectionEffect::Idle, 0);
  assert(!fixture.port.vibrationOn && !fixture.port.latchOn);
  assert(!fixture.sequence.IsBusy());
  assert(!fixture.sequence.NeedsRecovery());
  assert(fixture.sequence.SelectedCompartment() == 0);
  assert(fixture.sequence.Start(6, 2300, step));
}

void AdvanceToPhase(SelectionSequence& sequence, uint8_t phase,
                    SelectionControlMode controlMode =
                        SelectionControlMode::Standalone) {
  SelectionStep step;
  assert(sequence.Start(4, 0, controlMode, step));

  if (phase >= 1) {
    for (uint8_t compartment = 2;
         compartment <= SelectionSequence::CompartmentCount; ++compartment) {
      ExpectStep(sequence, (compartment - 1) * 100,
                 SelectionEffect::SearchStep, compartment);
    }
    ExpectStep(sequence, 600, SelectionEffect::Highlight, 4);
  }

  if (phase >= 2) {
    ExpectStep(sequence, 1000, SelectionEffect::VibrationOn, 4);
  }

  if (phase >= 3) {
    ExpectStep(sequence, 1150, SelectionEffect::VibrationOff, 4);
  }

  if (phase >= 4) {
    ExpectStep(sequence, 1300, SelectionEffect::VibrationOn, 4);
    ExpectStep(sequence, 1450, SelectionEffect::VibrationOff, 4);
    ExpectStep(sequence, 1600, SelectionEffect::VibrationOn, 4);
    ExpectStep(sequence, 1750, SelectionEffect::VibrationOff, 4);
    ExpectStep(sequence, 1900, SelectionEffect::ReleaseLatch, 4);
  }
}

void TestStopInEveryPhase() {
  for (uint8_t phase = 0; phase <= 4; ++phase) {
    Fixture fixture;
    fixture.Recover();
    AdvanceToPhase(fixture.sequence, phase);

    const int releaseCount = fixture.port.releaseCount;
    const int disableCount = fixture.port.disableCount;
    SelectionStep step;
    assert(fixture.sequence.Stop(step));
    assert(step.effect == SelectionEffect::Stopped);
    assert(step.compartment == 4);
    assert(fixture.port.disableCount == disableCount + 1);
    assert(!fixture.port.vibrationOn && !fixture.port.latchOn);
    assert(!fixture.sequence.IsBusy());
    assert(fixture.sequence.NeedsRecovery());
    assert(!fixture.sequence.Update(10000, step));
    assert(fixture.port.releaseCount == releaseCount);
    assert(!fixture.sequence.Start(2, 10000, step));
    assert(!fixture.sequence.Stop(step));
    assert(!fixture.port.vibrationOn && !fixture.port.latchOn);

    fixture.Recover();
    assert(fixture.sequence.SelectedCompartment() == 0);
    assert(fixture.sequence.Start(2, 10000, step));
  }
}

void TestStopWhileIdle() {
  Fixture fixture;
  SelectionStep step;
  assert(fixture.sequence.Stop(step));
  assert(step.effect == SelectionEffect::Stopped);
  assert(!fixture.sequence.Stop(step));
  assert(fixture.sequence.NeedsRecovery());
  fixture.Recover();
  assert(fixture.sequence.Stop(step));
  assert(!fixture.sequence.Start(1, 0, step));
  assert(fixture.port.releaseCount == 0);
}

void TestLateUpdatesAndLimits() {
  Fixture searchFixture;
  searchFixture.Recover();
  SelectionStep step;
  assert(searchFixture.sequence.Start(2, 0, step));
  ExpectStep(searchFixture.sequence, 1000, SelectionEffect::SearchStep, 2);
  assert(!searchFixture.sequence.Update(1000, step));
  ExpectStep(searchFixture.sequence, 5000, SelectionEffect::Stopped, 2);
  assert(searchFixture.sequence.NeedsRecovery());

  Fixture motorFixture;
  motorFixture.Recover();
  AdvanceToPhase(motorFixture.sequence, 2);
  ExpectStep(motorFixture.sequence, 1250, SelectionEffect::Stopped, 4);
  assert(!motorFixture.port.vibrationOn);
  assert(motorFixture.port.releaseCount == 0);

  Fixture servoFixture;
  servoFixture.Recover();
  AdvanceToPhase(servoFixture.sequence, 4);
  ExpectStep(servoFixture.sequence, 2400, SelectionEffect::Stopped, 4);
  assert(!servoFixture.port.latchOn);
  assert(servoFixture.port.releaseCount == 1);
}

void TestTimerWraparound() {
  Fixture fixture;
  fixture.Recover();
  SelectionStep step;
  assert(fixture.sequence.Start(1, UINT32_MAX - 50, step));
  assert(!fixture.sequence.Update(48, step));
  ExpectStep(fixture.sequence, 49, SelectionEffect::SearchStep, 2);
  ExpectStep(fixture.sequence, 4949, SelectionEffect::Stopped, 1);
  assert(!fixture.port.vibrationOn && !fixture.port.latchOn);
}

void TestExternalSafetyStop() {
  for (uint8_t phase = 0; phase <= 2; phase += 2) {
    Fixture fixture;
    fixture.Recover();
    AdvanceToPhase(fixture.sequence, phase);

    assert(fixture.safety.Stop());
    assert(!fixture.port.vibrationOn);
    ExpectStep(fixture.sequence, 1100, SelectionEffect::Stopped, 4);
    assert(!fixture.sequence.IsBusy());
    assert(fixture.sequence.NeedsRecovery());
    assert(fixture.port.releaseCount == 0);
  }
}

void TestMotorLimitAcrossWraparound() {
  Fixture fixture;
  fixture.Recover();
  SelectionStep step;
  const uint32_t startMs = UINT32_MAX - 1100;
  assert(fixture.sequence.Start(4, startMs, step));

  for (uint8_t compartment = 2;
       compartment <= SelectionSequence::CompartmentCount; ++compartment) {
    ExpectStep(fixture.sequence, startMs + (compartment - 1) * 100,
               SelectionEffect::SearchStep, compartment);
  }

  ExpectStep(fixture.sequence, startMs + 600, SelectionEffect::Highlight, 4);
  ExpectStep(fixture.sequence, startMs + 1000,
             SelectionEffect::VibrationOn, 4);
  ExpectStep(fixture.sequence, startMs + 1250,
             SelectionEffect::Stopped, 4);
  assert(!fixture.port.vibrationOn);
}

void TestLatchLimitAcrossWraparound() {
  Fixture fixture;
  fixture.Recover();
  SelectionStep step;
  const uint32_t startMs = UINT32_MAX - 2000;
  assert(fixture.sequence.Start(4, startMs, step));

  for (uint8_t compartment = 2;
       compartment <= SelectionSequence::CompartmentCount; ++compartment) {
    ExpectStep(fixture.sequence, startMs + (compartment - 1) * 100,
               SelectionEffect::SearchStep, compartment);
  }

  ExpectStep(fixture.sequence, startMs + 600, SelectionEffect::Highlight, 4);
  ExpectStep(fixture.sequence, startMs + 1000,
             SelectionEffect::VibrationOn, 4);
  ExpectStep(fixture.sequence, startMs + 1150,
             SelectionEffect::VibrationOff, 4);
  ExpectStep(fixture.sequence, startMs + 1300,
             SelectionEffect::VibrationOn, 4);
  ExpectStep(fixture.sequence, startMs + 1450,
             SelectionEffect::VibrationOff, 4);
  ExpectStep(fixture.sequence, startMs + 1600,
             SelectionEffect::VibrationOn, 4);
  ExpectStep(fixture.sequence, startMs + 1750,
             SelectionEffect::VibrationOff, 4);
  ExpectStep(fixture.sequence, startMs + 1900,
             SelectionEffect::ReleaseLatch, 4);
  ExpectStep(fixture.sequence, startMs + 2400,
             SelectionEffect::Stopped, 4);
  assert(!fixture.port.latchOn);
}

void TestManualLeaseLoss() {
  for (uint8_t phase = 0; phase <= 4; ++phase) {
    Fixture fixture;
    fixture.Recover();
    AdvanceToPhase(fixture.sequence, phase, SelectionControlMode::Manual);

    const int releaseCount = fixture.port.releaseCount;
    SelectionStep step;
    assert(fixture.sequence.ReportManualLeaseLost(step));
    assert(step.effect == SelectionEffect::Stopped);
    assert(!fixture.port.vibrationOn && !fixture.port.latchOn);
    assert(!fixture.sequence.IsBusy());
    assert(fixture.sequence.NeedsRecovery());
    assert(fixture.port.releaseCount == releaseCount);
    assert(!fixture.sequence.Start(2, 2000, step));
  }

  Fixture standaloneFixture;
  standaloneFixture.Recover();
  AdvanceToPhase(standaloneFixture.sequence, 2);
  SelectionStep step;
  assert(!standaloneFixture.sequence.ReportManualLeaseLost(step));
  assert(standaloneFixture.port.vibrationOn);
  assert(standaloneFixture.sequence.IsBusy());
  assert(!standaloneFixture.sequence.NeedsRecovery());
}

void TestFaultAndStopPriority() {
  Fixture fixture;
  fixture.Recover();
  AdvanceToPhase(fixture.sequence, 4, SelectionControlMode::Manual);

  SelectionStep step;
  assert(fixture.sequence.Stop(step));
  const int releaseCount = fixture.port.releaseCount;
  assert(fixture.sequence.ReportFault(step));
  assert(fixture.safety.IsFaulted());
  assert(!fixture.sequence.ReportManualLeaseLost(step));
  assert(!fixture.port.vibrationOn && !fixture.port.latchOn);
  assert(fixture.port.releaseCount == releaseCount);
  assert(fixture.sequence.NeedsRecovery());

  fixture.Recover();
  assert(!fixture.safety.IsFaulted());
  assert(fixture.sequence.Start(2, 0, step));
  assert(fixture.sequence.ReportFault(step));
  assert(step.effect == SelectionEffect::Stopped);
  assert(fixture.safety.IsFaulted());
  assert(!fixture.sequence.ObserveControlLoopPass(250, step));
  assert(fixture.safety.IsFaulted());
  assert(fixture.sequence.Stop(step));
  assert(fixture.safety.IsFaulted());
  assert(!fixture.sequence.Start(3, 0, step));
  fixture.Recover();
  assert(!fixture.safety.IsFaulted());
}

void TestFaultWhileOutputsActive() {
  for (uint8_t phase = 2; phase <= 4; phase += 2) {
    Fixture fixture;
    fixture.Recover();
    AdvanceToPhase(fixture.sequence, phase);

    const int releaseCount = fixture.port.releaseCount;
    SelectionStep step;
    assert(fixture.sequence.ReportFault(step));
    assert(step.effect == SelectionEffect::Stopped);
    assert(!fixture.port.vibrationOn && !fixture.port.latchOn);
    assert(fixture.port.releaseCount == releaseCount);
    assert(fixture.safety.IsFaulted());
    assert(fixture.sequence.NeedsRecovery());
    assert(!fixture.sequence.Start(3, 2000, step));
  }
}

void AdvanceWithControlLoop(Fixture& fixture, uint32_t untilMs) {
  for (uint32_t nowMs = 50; nowMs <= untilMs; nowMs += 50) {
    SelectionStep step;
    assert(!fixture.sequence.ObserveControlLoopPass(nowMs, step));
    fixture.sequence.Update(nowMs, step);
  }
}

void TestControlLoopTimeout() {
  Fixture motorFixture;
  motorFixture.Recover();
  SelectionStep step;
  assert(motorFixture.sequence.Start(4, 0, step));
  AdvanceWithControlLoop(motorFixture, 1000);
  assert(motorFixture.port.vibrationOn);
  assert(motorFixture.sequence.ObserveControlLoopPass(1250, step));
  assert(step.effect == SelectionEffect::Stopped);
  assert(!motorFixture.port.vibrationOn);
  assert(motorFixture.sequence.NeedsRecovery());

  Fixture latchFixture;
  latchFixture.Recover();
  assert(latchFixture.sequence.Start(4, 0, step));
  AdvanceWithControlLoop(latchFixture, 1900);
  assert(latchFixture.port.latchOn);
  assert(latchFixture.sequence.ObserveControlLoopPass(2150, step));
  assert(!latchFixture.port.latchOn);
  assert(latchFixture.port.releaseCount == 1);
  assert(latchFixture.sequence.NeedsRecovery());

  Fixture wrappedFixture;
  wrappedFixture.Recover();
  const uint32_t startMs = UINT32_MAX - 100;
  assert(wrappedFixture.sequence.Start(1, startMs, step));
  assert(!wrappedFixture.sequence.ObserveControlLoopPass(startMs + 249,
                                                         step));
  assert(wrappedFixture.sequence.ObserveControlLoopPass(startMs + 499,
                                                        step));
  assert(wrappedFixture.sequence.NeedsRecovery());
}

}  // namespace

int main() {
  TestCompleteSequence();
  TestStopInEveryPhase();
  TestStopWhileIdle();
  TestLateUpdatesAndLimits();
  TestTimerWraparound();
  TestExternalSafetyStop();
  TestMotorLimitAcrossWraparound();
  TestLatchLimitAcrossWraparound();
  TestManualLeaseLoss();
  TestFaultAndStopPriority();
  TestFaultWhileOutputsActive();
  TestControlLoopTimeout();
}

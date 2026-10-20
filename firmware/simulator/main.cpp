#include <stdint.h>

#include <cstdlib>
#include <cstring>
#include <iostream>
#include <string>

#include "../src/features/device_control/DeviceControl.h"
#include "../src/features/safety/SafetyController.h"

namespace {

void Event(uint64_t nowMs, const std::string& name, int value = -1) {
  std::cout << "SIMULACE t=" << nowMs << " event=" << name;
  if (value >= 0) {
    std::cout << " value=" << value;
  }
  std::cout << '\n';
}

class TimelinePort : public SafetyPort {
 public:
  void DisableActuators() override {
    motorOn = false;
    latchOn = false;
    Event(nowMs, "actuators_off");
  }

  void StartVibration(uint8_t compartment) override {
    if (motorOn || latchOn) {
      std::abort();
    }

    motorOn = true;
    Event(nowMs, "vibration_on", compartment);
  }

  void ReleaseLatch(uint8_t compartment) override {
    if (motorOn || latchOn) {
      std::abort();
    }

    latchOn = true;
    ++releaseCount;
    Event(nowMs, "release_latch", compartment);
  }

  uint64_t nowMs = 0;
  bool motorOn = false;
  bool latchOn = false;
  int releaseCount = 0;
};

class TimelineJournal : public CommandJournal {
 public:
  bool HasPending() const override { return pending; }
  uint8_t PendingCompartment() const override { return compartment; }
  uint8_t ReadyMask() const override { return persistedReadyMask; }

  bool Begin(const std::string&, uint8_t selected) override {
    if (failBegin || pending) {
      Event(nowMs, "journal_failed");
      return false;
    }

    pending = true;
    compartment = selected;
    ++beginCount;
    Event(nowMs, "journal_begin", selected);
    return true;
  }

  bool Resolve(uint8_t readyMask) override {
    if (!pending) {
      return false;
    }

    persistedReadyMask = readyMask;
    pending = false;
    compartment = 0;
    ++resolveCount;
    Event(nowMs, "journal_resolve", readyMask);
    return true;
  }

  bool PersistReadyMask(uint8_t readyMask) override {
    if (pending) {
      return false;
    }
    persistedReadyMask = readyMask;
    Event(nowMs, "journal_reload", readyMask);
    return true;
  }

  uint64_t nowMs = 0;
  bool failBegin = false;
  bool pending = false;
  uint8_t compartment = 0;
  uint8_t persistedReadyMask = 63;
  int beginCount = 0;
  int resolveCount = 0;
};

void PrintStep(uint64_t nowMs, const SelectionStep& step) {
  switch (step.effect) {
    case SelectionEffect::SearchStep:
      Event(nowMs, "search", step.compartment);
      break;
    case SelectionEffect::Highlight:
      Event(nowMs, "highlight", step.compartment);
      break;
    case SelectionEffect::Stopped:
      Event(nowMs, "stopped", step.compartment);
      break;
    case SelectionEffect::Idle:
      Event(nowMs, "completed");
      break;
    case SelectionEffect::VibrationOn:
    case SelectionEffect::VibrationOff:
    case SelectionEffect::ReleaseLatch:
      break;
  }
}

void TickUntil(DeviceControl& control, TimelinePort& port,
               TimelineJournal& journal, uint64_t firstMs, uint64_t lastMs) {
  for (uint64_t nowMs = firstMs; nowMs <= lastMs; nowMs += 50) {
    port.nowMs = nowMs;
    journal.nowMs = nowMs;
    SelectionStep step;
    if (control.Tick(nowMs, step)) {
      PrintStep(nowMs, step);
    }
  }
}

bool IsScenario(const std::string& value) {
  return value == "normal" || value == "lost-ack" || value == "stop" ||
         value == "lease-loss" || value == "restart" ||
         value == "journal-failure";
}

}  // namespace

int main(int argc, char* argv[]) {
  if (argc != 4 || !IsScenario(argv[1])) {
    std::cerr << "usage: simulator <scenario> <compartment 1..6> <commandId>\n";
    return 2;
  }

  const std::string scenario = argv[1];
  if (std::strlen(argv[2]) != 1 || argv[2][0] < '1' || argv[2][0] > '6' ||
      argv[3][0] == '\0') {
    std::cerr << "invalid simulator input\n";
    return 2;
  }
  const uint8_t compartment = static_cast<uint8_t>(argv[2][0] - '0');

  TimelinePort port;
  TimelineJournal journal;
  SafetyController safety(port);
  SelectionSequence sequence(safety);
  DeviceControl control("kufr-001", "boot-1", sequence, journal, 1);
  safety.InitializeSafe();
  if (!control.RecoverAfterInspection() ||
      !control.AcquireManualControl("lease-1", 0)) {
    return 1;
  }

  SelectionCommand command;
  command.commandId = argv[3];
  command.deviceId = "kufr-001";
  command.bootId = "boot-1";
  command.controlLeaseId = "lease-1";
  command.sequence = 1;
  command.expectedRevision = control.Revision();
  command.issuedAtDeviceMs = 0;
  command.maxAgeMs = 2000;
  command.compartment = compartment;

  journal.failBegin = scenario == "journal-failure";
  const CommandReceipt receipt = control.AcceptSelection(command, true, 0);
  if (receipt.error == CommandError::JournalFailed) {
    Event(0, "ack_rejected_journal_failed");
    Event(0, "ready_mask", control.ReadyMask());
    return 0;
  }
  if (receipt.outcome != CommandOutcome::Accepted) {
    std::cerr << "simulator command was not accepted\n";
    return 1;
  }

  Event(0, "ack_accepted");
  uint64_t startMs = 0;
  if (scenario == "lost-ack") {
    for (uint64_t retryMs : {500u, 1000u}) {
      const CommandReceipt repeated =
          control.AcceptSelection(command, true, retryMs);
      if (!repeated.duplicate || repeated.outcome != CommandOutcome::Accepted) {
        return 1;
      }
      Event(retryMs, "ack_duplicate");
    }
    startMs = 1000;
  }

  port.nowMs = startMs;
  journal.nowMs = startMs;
  SelectionStep step;
  if (!control.BeginAcceptedSelection(startMs, step)) {
    return 1;
  }
  PrintStep(startMs, step);

  if (scenario == "stop" || scenario == "lease-loss") {
    TickUntil(control, port, journal, startMs + 50, startMs + 1000);
    port.nowMs = startMs + 1000;
    journal.nowMs = startMs + 1000;
    if (scenario == "stop") {
      control.Stop(step);
    } else {
      control.DisconnectManualControl(step);
    }
    PrintStep(startMs + 1000, step);
  } else if (scenario == "restart") {
    TickUntil(control, port, journal, startMs + 50, startMs + 1900);
    port.nowMs = startMs + 1900;
    port.DisableActuators();
    Event(startMs + 1900, "power_reset");

    SafetyController rebootSafety(port);
    SelectionSequence rebootSequence(rebootSafety);
    DeviceControl rebooted("kufr-001", "boot-2", rebootSequence, journal, 1);
    rebootSafety.InitializeSafe();
    if (!rebooted.NeedsRecovery()) {
      return 1;
    }
    Event(startMs + 1900, "recovery_required");
    if (!rebooted.RecoverAfterInspection()) {
      return 1;
    }
    Event(startMs + 1900, "inspection_complete");
    Event(startMs + 1900, "ready_mask", rebooted.ReadyMask());
    return 0;
  } else {
    TickUntil(control, port, journal, startMs + 50, startMs + 2300);
  }

  Event(startMs + 2300, "journal_pending", journal.HasPending() ? 1 : 0);
  Event(startMs + 2300, "ready_mask", control.ReadyMask());
  return 0;
}

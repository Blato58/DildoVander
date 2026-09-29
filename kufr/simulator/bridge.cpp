#include <stdint.h>

#include <iostream>
#include <sstream>
#include <string>

#include "../firmware/src/features/device_control/DeviceControl.h"
#include "../firmware/src/features/safety/SafetyController.h"

namespace {

const char* ErrorName(CommandError error) {
  switch (error) {
    case CommandError::None: return "none";
    case CommandError::InvalidMessage: return "invalid_message";
    case CommandError::Unauthorized: return "unauthorized";
    case CommandError::WrongBoot: return "wrong_boot";
    case CommandError::LeaseExpired: return "lease_expired";
    case CommandError::Expired: return "expired";
    case CommandError::StaleRevision: return "stale_revision";
    case CommandError::SequenceGap: return "sequence_gap";
    case CommandError::AlreadySeenResultUnavailable: return "already_seen_result_unavailable";
    case CommandError::IdConflict: return "id_conflict";
    case CommandError::Busy: return "busy";
    case CommandError::CompartmentUnavailable: return "compartment_unavailable";
    case CommandError::NotCalibrated: return "not_calibrated";
    case CommandError::SafetyLatched: return "safety_latched";
    case CommandError::JournalFailed: return "journal_failed";
  }
  return "invalid_message";
}

const char* OutcomeName(CommandOutcome outcome) {
  switch (outcome) {
    case CommandOutcome::Rejected: return "rejected";
    case CommandOutcome::Accepted: return "accepted";
    case CommandOutcome::Completed: return "completed";
    case CommandOutcome::OutcomeUnknown: return "outcome_unknown";
  }
  return "outcome_unknown";
}

class BridgePort : public SafetyPort {
 public:
  void DisableActuators() override {
    motorOn_ = false;
    latchOn_ = false;
    Emit("actuators_off", 0);
  }

  void StartVibration(uint8_t compartment) override {
    if (motorOn_ || latchOn_) {
      std::abort();
    }
    motorOn_ = true;
    Emit("vibration_on", compartment);
  }

  void ReleaseLatch(uint8_t compartment) override {
    if (motorOn_ || latchOn_) {
      std::abort();
    }
    latchOn_ = true;
    Emit("release_latch", compartment);
  }

  void Emit(const char* event, int compartment) const {
    std::cout << "E {\"timeMs\":" << nowMs
              << ",\"event\":\"" << event
              << "\",\"compartment\":" << compartment << "}" << std::endl;
  }

  uint64_t nowMs = 0;

 private:
  bool motorOn_ = false;
  bool latchOn_ = false;
};

class BridgeJournal : public CommandJournal {
 public:
  bool HasPending() const override { return pending_; }
  uint8_t PendingCompartment() const override { return compartment_; }
  uint8_t ReadyMask() const override { return readyMask_; }

  bool Begin(const std::string&, uint8_t compartment) override {
    if (pending_) {
      return false;
    }
    pending_ = true;
    compartment_ = compartment;
    return true;
  }

  bool Resolve(uint8_t readyMask) override {
    if (!pending_) {
      return false;
    }
    readyMask_ = readyMask;
    pending_ = false;
    compartment_ = 0;
    return true;
  }

 private:
  bool pending_ = false;
  uint8_t compartment_ = 0;
  uint8_t readyMask_ = 63;
};

void EmitStep(BridgePort& port, const SelectionStep& step,
              std::string& phase) {
  switch (step.effect) {
    case SelectionEffect::SearchStep:
      phase = "searching";
      port.Emit("search", step.compartment);
      break;
    case SelectionEffect::Highlight:
      phase = "highlighting";
      port.Emit("highlight", step.compartment);
      break;
    case SelectionEffect::VibrationOn:
      phase = "vibrating";
      break;
    case SelectionEffect::VibrationOff:
      phase = "highlighting";
      break;
    case SelectionEffect::ReleaseLatch:
      phase = "releasing";
      break;
    case SelectionEffect::Idle:
      phase = "ready";
      port.Emit("completed", 0);
      break;
    case SelectionEffect::Stopped:
      phase = "stopped";
      port.Emit("stopped", step.compartment);
      break;
  }
}

bool ReadEnd(std::istringstream& input) {
  std::string extra;
  return !(input >> extra);
}

}  // namespace

int main(int argc, char* argv[]) {
  if (argc != 2 || argv[1][0] == '\0') {
    return 2;
  }

  const std::string bootId = argv[1];
  BridgePort port;
  BridgeJournal journal;
  SafetyController safety(port);
  SelectionSequence sequence(safety);
  DeviceControl control("kufr-001", bootId, sequence, journal, 1);
  safety.InitializeSafe();
  if (!control.RecoverAfterInspection()) {
    return 1;
  }

  std::string leaseId;
  uint64_t leaseExpiresAtMs = 0;
  std::string activeCommandId;
  std::string lastCommandId;
  std::string lastOutcome;
  std::string phase = "ready";
  uint8_t lastCompartment = 0;
  bool success = false;
  bool duplicate = false;
  std::string error = "none";
  std::string outcome = "none";
  std::string line;

  while (std::getline(std::cin, line)) {
    std::istringstream input(line);
    std::string operation;
    input >> operation;
    success = false;
    duplicate = false;
    error = "none";
    outcome = "none";
    SelectionStep step;
    uint64_t nowMs = 0;

    if (operation == "ACQUIRE") {
      std::string requestedLease;
      if ((input >> requestedLease >> nowMs) && ReadEnd(input)) {
        success = control.AcquireManualControl(requestedLease, nowMs);
        if (success) {
          leaseId = requestedLease;
          leaseExpiresAtMs = nowMs + DeviceControl::ManualLeaseDurationMs;
        }
      }
    } else if (operation == "RENEW") {
      std::string requestedLease;
      if ((input >> requestedLease >> nowMs) && ReadEnd(input)) {
        success = control.RenewManualLease(requestedLease, nowMs);
        if (requestedLease == leaseId) {
          if (success) {
            leaseExpiresAtMs = nowMs + DeviceControl::ManualLeaseDurationMs;
          } else {
            leaseId.clear();
            leaseExpiresAtMs = 0;
            if (!activeCommandId.empty()) {
              lastOutcome = "outcome_unknown";
              activeCommandId.clear();
              phase = "stopped";
              port.Emit("stopped", lastCompartment);
            }
          }
        }
      }
    } else if (operation == "SELECT") {
      SelectionCommand command;
      unsigned int compartment = 0;
      std::string source;
      if ((input >> command.commandId >> command.deviceId >> command.bootId >>
           command.controlLeaseId >> command.sequence >>
           command.expectedRevision >> command.issuedAtDeviceMs >>
           command.maxAgeMs >> compartment >> source >> nowMs) &&
          ReadEnd(input) && compartment <= 255) {
        command.compartment = static_cast<uint8_t>(compartment);
        if (source == "operatorConfirmedAi") {
          command.source = SelectionSource::OperatorConfirmedAi;
        } else if (source == "operatorCorrectedAi") {
          command.source = SelectionSource::OperatorCorrectedAi;
        } else if (source == "buttonConfirmedAi") {
          command.source = SelectionSource::ButtonConfirmedAi;
        } else {
          command.source = SelectionSource::Manual;
        }
        const CommandReceipt receipt = control.AcceptSelection(command, true, nowMs);
        success = receipt.error == CommandError::None;
        duplicate = receipt.duplicate;
        error = ErrorName(receipt.error);
        outcome = OutcomeName(receipt.outcome);
        if (receipt.error == CommandError::LeaseExpired &&
            nowMs >= leaseExpiresAtMs) {
          leaseId.clear();
          leaseExpiresAtMs = 0;
          if (!activeCommandId.empty()) {
            lastOutcome = "outcome_unknown";
            activeCommandId.clear();
            phase = "stopped";
            port.Emit("stopped", lastCompartment);
          }
        }
        if (success && !duplicate) {
          activeCommandId = command.commandId;
          lastCommandId = command.commandId;
          lastCompartment = command.compartment;
          lastOutcome = "accepted";
        }
      }
    } else if (operation == "BEGIN") {
      if ((input >> nowMs) && ReadEnd(input)) {
        port.nowMs = nowMs;
        success = control.BeginAcceptedSelection(nowMs, step);
        if (success) {
          EmitStep(port, step, phase);
        } else if (nowMs >= leaseExpiresAtMs && !leaseId.empty()) {
          leaseId.clear();
          leaseExpiresAtMs = 0;
          if (!activeCommandId.empty()) {
            lastOutcome = "outcome_unknown";
            activeCommandId.clear();
            phase = "stopped";
            port.Emit("stopped", lastCompartment);
          }
        }
      }
    } else if (operation == "TICK") {
      if ((input >> nowMs) && ReadEnd(input)) {
        port.nowMs = nowMs;
        success = control.Tick(nowMs, step);
        if (success) {
          EmitStep(port, step, phase);
          if (step.effect == SelectionEffect::Idle ||
              step.effect == SelectionEffect::Stopped) {
            lastOutcome = step.effect == SelectionEffect::Idle
                              ? "completed" : "outcome_unknown";
            activeCommandId.clear();
          }
        }
        if (!leaseId.empty() && nowMs >= leaseExpiresAtMs) {
          leaseId.clear();
          leaseExpiresAtMs = 0;
        }
      }
    } else if (operation == "STOP" || operation == "DISCONNECT") {
      if ((input >> nowMs) && ReadEnd(input)) {
        port.nowMs = nowMs;
        success = operation == "STOP" ? control.Stop(step)
                                        : control.DisconnectManualControl(step);
        if (success) {
          EmitStep(port, step, phase);
        }
        if (operation == "DISCONNECT") {
          leaseId.clear();
          leaseExpiresAtMs = 0;
        }
        if (!activeCommandId.empty()) {
          lastOutcome = "outcome_unknown";
          activeCommandId.clear();
        }
      }
    } else if (operation == "RECOVER") {
      if ((input >> nowMs) && ReadEnd(input)) {
        success = control.RecoverAfterInspection();
        if (success) {
          phase = "ready";
        }
      }
    } else if (operation == "SNAPSHOT") {
      if ((input >> nowMs) && ReadEnd(input)) {
        success = true;
      }
    }

    if (!success && error == "none" && operation != "TICK" &&
        operation != "STOP" && operation != "DISCONNECT") {
      error = "invalid_message";
    }

    std::cout << "R {\"operation\":\"" << operation
              << "\",\"success\":" << (success ? "true" : "false")
              << ",\"duplicate\":" << (duplicate ? "true" : "false")
              << ",\"error\":\"" << error
              << "\",\"outcome\":\"" << outcome
              << "\",\"revision\":" << control.Revision()
              << ",\"readyMask\":" << static_cast<int>(control.ReadyMask())
              << ",\"needsRecovery\":"
              << (control.NeedsRecovery() ? "true" : "false")
              << ",\"phase\":\"" << phase
              << "\",\"leaseId\":\"" << leaseId
              << "\",\"activeCommandId\":\"" << activeCommandId
              << "\",\"lastCommandId\":\"" << lastCommandId
              << "\",\"lastCompartment\":" << static_cast<int>(lastCompartment)
              << ",\"lastOutcome\":\"" << lastOutcome
              << "\",\"journalPending\":"
              << (journal.HasPending() ? "true" : "false")
              << "}" << std::endl;
  }

  return 0;
}

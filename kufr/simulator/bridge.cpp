#include <stdint.h>

#include <cerrno>
#include <fstream>
#include <iostream>
#include <iterator>
#include <sstream>
#include <string>

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#else
#include <fcntl.h>
#include <sys/file.h>
#include <unistd.h>
#endif

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

bool ReadEnd(std::istringstream& input);

class JournalLock {
 public:
  explicit JournalLock(const std::string& journalPath) {
    const std::string lockPath = journalPath + ".lock";
#ifdef _WIN32
    handle_ = CreateFileA(lockPath.c_str(), GENERIC_READ | GENERIC_WRITE, 0,
                          nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    owns_ = handle_ != INVALID_HANDLE_VALUE;
    newlyCreated_ = owns_ && GetLastError() != ERROR_ALREADY_EXISTS;
#else
    const bool existed = access(lockPath.c_str(), F_OK) == 0;
    fileDescriptor_ = open(lockPath.c_str(), O_CREAT | O_RDWR, 0600);
    owns_ = fileDescriptor_ >= 0 && flock(fileDescriptor_, LOCK_EX | LOCK_NB) == 0;
    newlyCreated_ = owns_ && !existed;
#endif
  }

  ~JournalLock() {
#ifdef _WIN32
    if (owns_) {
      CloseHandle(handle_);
    }
#else
    if (fileDescriptor_ >= 0) {
      close(fileDescriptor_);
    }
#endif
  }

  bool Owns() const { return owns_; }
  bool NewlyCreated() const { return newlyCreated_; }

 private:
#ifdef _WIN32
  HANDLE handle_ = INVALID_HANDLE_VALUE;
#else
  int fileDescriptor_ = -1;
#endif
  bool owns_ = false;
  bool newlyCreated_ = false;
};

class BridgeJournal : public CommandJournal {
 public:
  BridgeJournal(const std::string& path, bool allowCreate) : path_(path) {
    Load(allowCreate);
  }

  bool HasPending() const override { return fault_ || pending_; }
  uint8_t PendingCompartment() const override { return compartment_; }
  uint8_t ReadyMask() const override { return fault_ ? 0 : readyMask_; }
  bool HasFault() const { return fault_; }

  bool Begin(const std::string& commandId, uint8_t compartment) override {
    if (fault_ || pending_ || compartment < 1 || compartment > 6) {
      return false;
    }
    if (!Append("B " + commandId + " " + std::to_string(compartment) + "\n")) {
      fault_ = true;
      return false;
    }
    pending_ = true;
    compartment_ = compartment;
    return true;
  }

  bool Resolve(uint8_t readyMask) override {
    if (fault_ || !pending_ || readyMask > 63) {
      return false;
    }
    if (!Append("R " + std::to_string(readyMask) + "\n")) {
      fault_ = true;
      return false;
    }
    readyMask_ = readyMask;
    pending_ = false;
    compartment_ = 0;
    return true;
  }

 private:
  bool Append(const std::string& record) {
    std::ofstream output(path_.c_str(), std::ios::binary | std::ios::app);
    if (!output) {
      return false;
    }
    output << record;
    output.flush();
    return output.good();
  }

  void Load(bool allowCreate) {
    errno = 0;
    std::ifstream input(path_.c_str(), std::ios::binary);
    if (!input) {
      if (errno == ENOENT && allowCreate) {
        fault_ = !Append("I 63\n");
      } else {
        fault_ = true;
      }
      return;
    }

    const std::string contents((std::istreambuf_iterator<char>(input)),
                               std::istreambuf_iterator<char>());
    if (input.bad()) {
      fault_ = true;
      return;
    }

    size_t cursor = 0;
    bool initialized = false;
    while (cursor < contents.size()) {
      const size_t end = contents.find('\n', cursor);
      if (end == std::string::npos) {
        fault_ = true;
        return;
      }

      std::istringstream record(contents.substr(cursor, end - cursor));
      char operation = 0;
      unsigned int value = 0;
      record >> operation;
      if (operation == 'I' && !initialized && cursor == 0) {
        if (!(record >> value) || value != 63 || !ReadEnd(record)) {
          fault_ = true;
          return;
        }
        initialized = true;
      } else if (operation == 'B' && initialized) {
        std::string commandId;
        if (pending_ || !(record >> commandId >> value) ||
            commandId.empty() || value < 1 || value > 6 || !ReadEnd(record)) {
          fault_ = true;
          return;
        }
        pending_ = true;
        compartment_ = static_cast<uint8_t>(value);
      } else if (operation == 'R' && initialized) {
        if (!pending_ || !(record >> value) || value > 63 || !ReadEnd(record)) {
          fault_ = true;
          return;
        }
        pending_ = false;
        compartment_ = 0;
        readyMask_ = static_cast<uint8_t>(value);
      } else {
        fault_ = true;
        return;
      }
      cursor = end + 1;
    }
    if (!initialized) {
      fault_ = true;
    }
  }

  std::string path_;
  bool fault_ = false;
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
  if (argc != 3 || argv[1][0] == '\0' || argv[2][0] == '\0') {
    return 2;
  }

  const std::string bootId = argv[1];
  JournalLock journalLock(argv[2]);
  if (!journalLock.Owns()) {
    return 3;
  }
  BridgePort port;
  BridgeJournal journal(argv[2], journalLock.NewlyCreated());
  SafetyController safety(port);
  SelectionSequence sequence(safety);
  DeviceControl control("kufr-001", bootId, sequence, journal, 1);
  safety.InitializeSafe();
  if (!journal.HasPending() && !control.RecoverAfterInspection()) {
    return 1;
  }

  std::string leaseId;
  uint64_t leaseExpiresAtMs = 0;
  std::string activeCommandId;
  std::string lastCommandId;
  std::string lastOutcome;
  std::string phase = journal.HasPending() ? "recoveryRequired" : "ready";
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
          if (!activeCommandId.empty() &&
              (step.effect == SelectionEffect::Idle ||
               step.effect == SelectionEffect::Stopped)) {
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
              << ",\"journalFault\":"
              << (journal.HasFault() ? "true" : "false")
              << "}" << std::endl;
  }

  return 0;
}

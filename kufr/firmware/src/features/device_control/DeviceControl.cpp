#include "DeviceControl.h"

bool SelectionCommand::SameContentAs(const SelectionCommand& other) const {
  return commandId == other.commandId && deviceId == other.deviceId &&
         bootId == other.bootId && controlLeaseId == other.controlLeaseId &&
         sequence == other.sequence &&
         expectedRevision == other.expectedRevision &&
         issuedAtDeviceMs == other.issuedAtDeviceMs &&
         maxAgeMs == other.maxAgeMs && compartment == other.compartment &&
         source == other.source;
}

bool InventoryReloadCommand::SameContentAs(
    const InventoryReloadCommand& other) const {
  return commandId == other.commandId && deviceId == other.deviceId &&
         bootId == other.bootId && controlLeaseId == other.controlLeaseId &&
         sequence == other.sequence &&
         expectedRevision == other.expectedRevision &&
         issuedAtDeviceMs == other.issuedAtDeviceMs &&
         maxAgeMs == other.maxAgeMs && compartment == other.compartment &&
         inspectionConfirmed == other.inspectionConfirmed;
}

DeviceControl::DeviceControl(const std::string& deviceId,
                             const std::string& bootId,
                             SelectionSequence& selection,
                             CommandJournal& journal,
                             uint64_t calibrationRevision)
    : deviceId_(deviceId),
      bootId_(bootId),
      selection_(selection),
      journal_(journal),
      readyMask_(journal.ReadyMask()),
      calibrationRevision_(calibrationRevision) {}

bool DeviceControl::RecoverAfterInspection() {
  if (selection_.IsBusy() || HasPendingSelection()) {
    return false;
  }

  if (journal_.HasPending()) {
    const uint8_t compartment = journal_.PendingCompartment();
    if (compartment >= 1 && compartment <= SelectionSequence::CompartmentCount) {
      readyMask_ &= static_cast<uint8_t>(~(1u << (compartment - 1)));
    }

    if (!journal_.Resolve(readyMask_)) {
      return false;
    }
  }

  if (!selection_.RecoverAfterInspection()) {
    return false;
  }

  ++revision_;
  return true;
}

bool DeviceControl::AcquireManualControl(const std::string& leaseId,
                                          uint64_t nowMs) {
  if (leaseId.empty() || !manualLeaseId_.empty() || selection_.IsBusy() ||
      selection_.NeedsRecovery() || journal_.HasPending()) {
    return false;
  }

  manualLeaseId_ = leaseId;
  leaseExpiresAtMs_ = nowMs + ManualLeaseDurationMs;
  lastSequence_ = 0;
  ++revision_;
  return true;
}

bool DeviceControl::RenewManualLease(const std::string& leaseId,
                                      uint64_t nowMs) {
  if (manualLeaseId_.empty() || manualLeaseId_ != leaseId) {
    return false;
  }

  if (nowMs >= leaseExpiresAtMs_) {
    SelectionStep step;
    LoseManualControl(step);
    return false;
  }

  leaseExpiresAtMs_ = nowMs + ManualLeaseDurationMs;
  return true;
}

bool DeviceControl::DisconnectManualControl(SelectionStep& step) {
  return LoseManualControl(step);
}

CommandReceipt DeviceControl::AcceptSelection(const SelectionCommand& command,
                                               bool authenticated,
                                               uint64_t nowMs) {
  if (!authenticated || command.deviceId != deviceId_) {
    return {CommandError::Unauthorized, CommandOutcome::Rejected, false};
  }

  if (command.bootId != bootId_) {
    return {CommandError::WrongBoot, CommandOutcome::Rejected, false};
  }

  if (manualLeaseId_.empty() || command.controlLeaseId != manualLeaseId_) {
    return {CommandError::LeaseExpired, CommandOutcome::Rejected, false};
  }

  if (nowMs >= leaseExpiresAtMs_) {
    SelectionStep step;
    LoseManualControl(step);
    return {CommandError::LeaseExpired, CommandOutcome::Rejected, false};
  }

  CachedResult* cached = FindCached(command.commandId);
  if (cached != nullptr) {
    if (cached->kind != ControlCommandKind::Selection ||
        !cached->selectionCommand.SameContentAs(command)) {
      return {CommandError::IdConflict, CommandOutcome::Rejected, false};
    }

    if (command.issuedAtDeviceMs > nowMs + 500 ||
        nowMs > command.issuedAtDeviceMs + command.maxAgeMs) {
      return {CommandError::Expired, CommandOutcome::Rejected, false};
    }

    CommandReceipt repeated = cached->receipt;
    repeated.duplicate = true;
    return repeated;
  }

  // A malformed envelope never consumes the manual sequence number.
  if (command.commandId.empty() || command.sequence == 0 ||
      command.compartment < 1 ||
      command.compartment > SelectionSequence::CompartmentCount ||
      command.maxAgeMs < 1 || command.maxAgeMs > 2000) {
    return {CommandError::InvalidMessage, CommandOutcome::Rejected, false};
  }

  if (command.sequence <= lastSequence_) {
    return {CommandError::AlreadySeenResultUnavailable,
            CommandOutcome::Rejected, false};
  }

  if (command.sequence != lastSequence_ + 1) {
    return {CommandError::SequenceGap, CommandOutcome::Rejected, false};
  }

  lastSequence_ = command.sequence;

  if (command.issuedAtDeviceMs > nowMs + 500 ||
      nowMs > command.issuedAtDeviceMs + command.maxAgeMs) {
    return Reject(command, CommandError::Expired, true);
  }

  if (command.expectedRevision != revision_) {
    return Reject(command, CommandError::StaleRevision, true);
  }

  if (selection_.NeedsRecovery()) {
    return Reject(command, CommandError::SafetyLatched, true);
  }

  if (HasPendingSelection() || selection_.IsBusy()) {
    return Reject(command, CommandError::Busy, true);
  }

  if (calibrationRevision_ == 0) {
    return Reject(command, CommandError::NotCalibrated, true);
  }

  if ((readyMask_ & (1u << (command.compartment - 1))) == 0) {
    return Reject(command, CommandError::CompartmentUnavailable, true);
  }

  if (!journal_.Begin(command.commandId, command.compartment)) {
    return Reject(command, CommandError::JournalFailed, true);
  }

  pendingCommandId_ = command.commandId;
  pendingCompartment_ = command.compartment;
  executionStarted_ = false;
  ++revision_;

  const CommandReceipt accepted = {CommandError::None,
                                   CommandOutcome::Accepted, false};
  Remember(command, accepted);
  return accepted;
}

CommandReceipt DeviceControl::ConfirmReload(
    const InventoryReloadCommand& command, bool authenticated,
    uint64_t nowMs) {
  if (!authenticated || command.deviceId != deviceId_) {
    return {CommandError::Unauthorized, CommandOutcome::Rejected, false};
  }
  if (command.bootId != bootId_) {
    return {CommandError::WrongBoot, CommandOutcome::Rejected, false};
  }
  if (manualLeaseId_.empty() || command.controlLeaseId != manualLeaseId_) {
    return {CommandError::LeaseExpired, CommandOutcome::Rejected, false};
  }
  if (nowMs >= leaseExpiresAtMs_) {
    SelectionStep step;
    LoseManualControl(step);
    return {CommandError::LeaseExpired, CommandOutcome::Rejected, false};
  }

  CachedResult* cached = FindCached(command.commandId);
  if (cached != nullptr) {
    if (cached->kind != ControlCommandKind::InventoryReload ||
        !cached->reloadCommand.SameContentAs(command)) {
      return {CommandError::IdConflict, CommandOutcome::Rejected, false};
    }
    if (command.issuedAtDeviceMs > nowMs + 500 ||
        nowMs > command.issuedAtDeviceMs + command.maxAgeMs) {
      return {CommandError::Expired, CommandOutcome::Rejected, false};
    }
    CommandReceipt repeated = cached->receipt;
    repeated.duplicate = true;
    return repeated;
  }

  if (command.commandId.empty() || command.sequence == 0 ||
      command.compartment < 1 ||
      command.compartment > SelectionSequence::CompartmentCount ||
      command.maxAgeMs < 1 || command.maxAgeMs > 2000 ||
      !command.inspectionConfirmed) {
    return {CommandError::InvalidMessage, CommandOutcome::Rejected, false};
  }
  if (command.sequence <= lastSequence_) {
    return {CommandError::AlreadySeenResultUnavailable,
            CommandOutcome::Rejected, false};
  }
  if (command.sequence != lastSequence_ + 1) {
    return {CommandError::SequenceGap, CommandOutcome::Rejected, false};
  }
  lastSequence_ = command.sequence;

  const auto reject = [&](CommandError error) {
    const CommandReceipt rejected = {error, CommandOutcome::Rejected, false};
    Remember(command, rejected);
    return rejected;
  };
  if (command.issuedAtDeviceMs > nowMs + 500 ||
      nowMs > command.issuedAtDeviceMs + command.maxAgeMs) {
    return reject(CommandError::Expired);
  }
  if (command.expectedRevision != revision_) {
    return reject(CommandError::StaleRevision);
  }
  if (HasPendingSelection() || selection_.IsBusy()) {
    return reject(CommandError::Busy);
  }
  if (selection_.NeedsRecovery() || journal_.HasPending()) {
    return reject(CommandError::SafetyLatched);
  }

  const uint8_t nextMask = static_cast<uint8_t>(
      readyMask_ | (1u << (command.compartment - 1)));
  if (nextMask != readyMask_) {
    if (!journal_.PersistReadyMask(nextMask)) {
      return reject(CommandError::JournalFailed);
    }
    readyMask_ = nextMask;
    ++revision_;
  }

  const CommandReceipt accepted = {CommandError::None,
                                   CommandOutcome::Accepted, false};
  Remember(command, accepted);
  return accepted;
}

bool DeviceControl::BeginAcceptedSelection(uint64_t nowMs,
                                            SelectionStep& step) {
  if (!HasPendingSelection() || executionStarted_) {
    return false;
  }

  if (manualLeaseId_.empty() || nowMs >= leaseExpiresAtMs_) {
    LoseManualControl(step);
    return false;
  }

  if (!selection_.Start(pendingCompartment_, static_cast<uint32_t>(nowMs),
                        SelectionControlMode::Manual, step)) {
    selection_.ReportFault(step);
    FinishCurrent(CommandOutcome::OutcomeUnknown);
    return false;
  }

  executionStarted_ = true;
  return true;
}

bool DeviceControl::Tick(uint64_t nowMs, SelectionStep& step) {
  if (!manualLeaseId_.empty() && nowMs >= leaseExpiresAtMs_) {
    return LoseManualControl(step);
  }

  if (!executionStarted_) {
    return false;
  }

  const uint32_t sequenceTimeMs = static_cast<uint32_t>(nowMs);
  if (selection_.ObserveControlLoopPass(sequenceTimeMs, step)) {
    FinishCurrent(CommandOutcome::OutcomeUnknown);
    return true;
  }

  if (!selection_.Update(sequenceTimeMs, step)) {
    return false;
  }

  if (step.effect == SelectionEffect::Idle) {
    const uint8_t compartment = pendingCompartment_;
    if (FinishCurrent(CommandOutcome::Completed) != CommandOutcome::Completed) {
      step = {SelectionEffect::Stopped, compartment};
    }
  } else if (step.effect == SelectionEffect::Stopped) {
    FinishCurrent(CommandOutcome::OutcomeUnknown);
  }

  return true;
}

bool DeviceControl::Stop(SelectionStep& step) {
  const bool newlyStopped = selection_.Stop(step);
  if (HasPendingSelection()) {
    FinishCurrent(CommandOutcome::OutcomeUnknown);
  }

  return newlyStopped;
}

uint64_t DeviceControl::Revision() const { return revision_; }

uint8_t DeviceControl::ReadyMask() const { return readyMask_; }

bool DeviceControl::NeedsRecovery() const {
  return selection_.NeedsRecovery() || journal_.HasPending();
}

bool DeviceControl::HasPendingSelection() const {
  return !pendingCommandId_.empty();
}

CommandReceipt DeviceControl::Reject(const SelectionCommand& command,
                                      CommandError error,
                                      bool consumeSequence) {
  if (consumeSequence) {
    lastSequence_ = command.sequence;
  }

  const CommandReceipt rejected = {error, CommandOutcome::Rejected, false};
  Remember(command, rejected);
  return rejected;
}

void DeviceControl::Remember(const SelectionCommand& command,
                              CommandReceipt receipt) {
  CachedResult& result = recentResults_[nextResultIndex_];
  result.occupied = true;
  result.kind = ControlCommandKind::Selection;
  result.selectionCommand = command;
  result.reloadCommand = InventoryReloadCommand();
  result.receipt = receipt;
  nextResultIndex_ = (nextResultIndex_ + 1) % ResultCacheSize;
}

void DeviceControl::Remember(const InventoryReloadCommand& command,
                             CommandReceipt receipt) {
  CachedResult& result = recentResults_[nextResultIndex_];
  result.occupied = true;
  result.kind = ControlCommandKind::InventoryReload;
  result.selectionCommand = SelectionCommand();
  result.reloadCommand = command;
  result.receipt = receipt;
  nextResultIndex_ = (nextResultIndex_ + 1) % ResultCacheSize;
}

DeviceControl::CachedResult* DeviceControl::FindCached(
    const std::string& commandId) {
  for (CachedResult& result : recentResults_) {
    if (result.occupied &&
        ((result.kind == ControlCommandKind::Selection &&
          result.selectionCommand.commandId == commandId) ||
         (result.kind == ControlCommandKind::InventoryReload &&
          result.reloadCommand.commandId == commandId))) {
      return &result;
    }
  }

  return nullptr;
}

CommandOutcome DeviceControl::FinishCurrent(CommandOutcome outcome) {
  if (!HasPendingSelection()) {
    return outcome;
  }

  readyMask_ &= static_cast<uint8_t>(~(1u << (pendingCompartment_ - 1)));
  // An interrupted release has an unknown physical result. Keep its durable
  // record until an operator inspects the compartment after Stop or reboot.
  if (outcome == CommandOutcome::Completed &&
      !journal_.Resolve(readyMask_)) {
    SelectionStep step;
    selection_.ReportFault(step);
    outcome = CommandOutcome::OutcomeUnknown;
  }

  CachedResult* result = FindCached(pendingCommandId_);
  if (result != nullptr) {
    result->receipt.outcome = outcome;
  }

  pendingCommandId_.clear();
  pendingCompartment_ = 0;
  executionStarted_ = false;
  ++revision_;
  return outcome;
}

bool DeviceControl::LoseManualControl(SelectionStep& step) {
  if (manualLeaseId_.empty()) {
    return false;
  }

  const bool newlyStopped = selection_.Stop(step);
  if (HasPendingSelection()) {
    FinishCurrent(CommandOutcome::OutcomeUnknown);
  }

  manualLeaseId_.clear();
  leaseExpiresAtMs_ = 0;
  ++revision_;
  return newlyStopped;
}

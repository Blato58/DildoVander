#include "DeviceControl.h"

namespace {

uint64_t SelectionFingerprint(const SelectionCommand& command) {
  uint64_t hash = 14695981039346656037ull;
  const auto number = [&hash](uint64_t value) {
    for (unsigned index = 0; index < 8; ++index) {
      hash ^= static_cast<uint8_t>(value >> (index * 8));
      hash *= 1099511628211ull;
    }
  };
  const auto text = [&hash, &number](const std::string& value) {
    number(value.size());
    for (unsigned char byte : value) {
      hash ^= byte;
      hash *= 1099511628211ull;
    }
  };
  text(command.commandId);
  text(command.deviceId);
  text(command.bootId);
  text(command.controlLeaseId);
  number(command.sequence);
  number(command.expectedRevision);
  number(command.issuedAtDeviceMs);
  number(command.maxAgeMs);
  number(command.compartment);
  number(static_cast<uint8_t>(command.source));
  number(command.operationEpoch);
  return hash == 0 ? 1 : hash;
}

}  // namespace

bool SelectionCommand::SameContentAs(const SelectionCommand& other) const {
  return commandId == other.commandId && deviceId == other.deviceId &&
         bootId == other.bootId && controlLeaseId == other.controlLeaseId &&
         sequence == other.sequence &&
         expectedRevision == other.expectedRevision &&
         issuedAtDeviceMs == other.issuedAtDeviceMs &&
         maxAgeMs == other.maxAgeMs && compartment == other.compartment &&
         source == other.source && operationEpoch == other.operationEpoch;
}

bool InventoryReloadCommand::SameContentAs(
    const InventoryReloadCommand& other) const {
  return commandId == other.commandId && deviceId == other.deviceId &&
         bootId == other.bootId && controlLeaseId == other.controlLeaseId &&
         sequence == other.sequence &&
         expectedRevision == other.expectedRevision &&
         issuedAtDeviceMs == other.issuedAtDeviceMs &&
         maxAgeMs == other.maxAgeMs && compartment == other.compartment &&
         inspectionConfirmed == other.inspectionConfirmed &&
         operationEpoch == other.operationEpoch;
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
  if (selection_.IsBusy() || HasPendingSelection() ||
      HasAutonomousPresentation()) {
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
  if (leaseId.empty() || !manualLeaseId_.empty() ||
      HasAutonomousPresentation() || selection_.IsBusy() ||
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

bool DeviceControl::ReleaseIdleManualControl(const std::string& leaseId, uint64_t nowMs) {
  if (!OwnsLiveManualLease(leaseId, nowMs) || NeedsRecovery() ||
      HasPendingSelection() || HasAutonomousPresentation() || selection_.IsBusy()) return false;
  manualLeaseId_.clear();
  leaseExpiresAtMs_ = 0;
  ++revision_;
  return true;
}

bool DeviceControl::OwnsManualLease(const std::string& leaseId) const {
  return !leaseId.empty() && manualLeaseId_ == leaseId;
}

bool DeviceControl::OwnsLiveManualLease(const std::string& leaseId,
                                        uint64_t nowMs) const {
  return OwnsManualLease(leaseId) && nowMs < leaseExpiresAtMs_;
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

  if (command.operationEpoch != OperationEpoch()) {
    return {CommandError::WrongOperationEpoch, CommandOutcome::Rejected, false};
  }

  if (manualLeaseId_.empty() || command.controlLeaseId != manualLeaseId_) {
    return {CommandError::LeaseExpired, CommandOutcome::Rejected, false};
  }

  if (nowMs >= leaseExpiresAtMs_) {
    SelectionStep step;
    LoseManualControl(step);
    return {CommandError::LeaseExpired, CommandOutcome::Rejected, false};
  }

  CachedResult* cached = FindCached(command.operationEpoch, command.commandId);
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

  if (HasPendingSelection() || HasAutonomousPresentation() ||
      selection_.IsBusy()) {
    return Reject(command, CommandError::Busy, true);
  }

  if (calibrationRevision_ == 0) {
    return Reject(command, CommandError::NotCalibrated, true);
  }

  if ((readyMask_ & (1u << (command.compartment - 1))) == 0) {
    return Reject(command, CommandError::CompartmentUnavailable, true);
  }

  const uint64_t fingerprint = command.operationEpoch == 0 ? 0 : SelectionFingerprint(command);
  if (command.operationEpoch != 0) {
    const auto existing = FindOperation(command.commandId, command.compartment,
                                         fingerprint, command.operationEpoch);
    if (existing == CommandJournal::Lookup::Same)
      return Reject(command, CommandError::AlreadySeenResultUnavailable, true);
    if (existing == CommandJournal::Lookup::Conflict)
      return Reject(command, CommandError::IdConflict, true);
    if (existing != CommandJournal::Lookup::Missing)
      return Reject(command, CommandError::JournalFailed, true);
  }
  const bool recorded = command.operationEpoch == 0
      ? journal_.Begin(command.commandId, command.compartment)
      : journal_.BeginScoped(command.operationEpoch, command.commandId,
                             command.compartment, fingerprint);
  if (!recorded) {
    return Reject(command, CommandError::JournalFailed, true);
  }

  pendingCommandId_ = command.commandId;
  pendingCompartment_ = command.compartment;
  pendingOperationEpoch_ = command.operationEpoch;
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
  if (command.operationEpoch != OperationEpoch()) {
    return {CommandError::WrongOperationEpoch, CommandOutcome::Rejected, false};
  }
  if (manualLeaseId_.empty() || command.controlLeaseId != manualLeaseId_) {
    return {CommandError::LeaseExpired, CommandOutcome::Rejected, false};
  }
  if (nowMs >= leaseExpiresAtMs_) {
    SelectionStep step;
    LoseManualControl(step);
    return {CommandError::LeaseExpired, CommandOutcome::Rejected, false};
  }

  CachedResult* cached = FindCached(command.operationEpoch, command.commandId);
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
  if (HasPendingSelection() || HasAutonomousPresentation() ||
      selection_.IsBusy()) {
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
  if (!HasPendingSelection() || executionStarted_ ||
      pendingOperationEpoch_ != OperationEpoch()) {
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

bool DeviceControl::BeginAutonomousPresentation(
    const std::string& proposalId, uint8_t compartment,
    uint64_t expectedRevision, uint64_t fingerprint, uint64_t operationEpoch) {
  if (operationEpoch != OperationEpoch() || (operationEpoch != 0 && fingerprint == 0) ||
      proposalId.empty() || compartment < 1 ||
      compartment > SelectionSequence::CompartmentCount ||
      expectedRevision != revision_ || calibrationRevision_ == 0 ||
      HasManualControl() || HasPendingSelection() ||
      HasAutonomousPresentation() || selection_.IsBusy() ||
      selection_.NeedsRecovery() || journal_.HasPending() ||
      (readyMask_ & (1u << (compartment - 1))) == 0) {
    return false;
  }

  // The durable record must exist before a servo port may request motion.
  if (operationEpoch != 0 &&
      FindOperation(proposalId, compartment, fingerprint, operationEpoch) !=
          CommandJournal::Lookup::Missing) return false;
  const bool recorded = operationEpoch == 0
      ? journal_.BeginWithIdentity(proposalId, compartment, fingerprint)
      : journal_.BeginScoped(operationEpoch, proposalId, compartment, fingerprint);
  if (!recorded) {
    SelectionStep step;
    selection_.ReportFault(step);
    return false;
  }
  autonomousCompartment_ = compartment;
  autonomousOperationId_ = proposalId;
  autonomousFingerprint_ = fingerprint;
  autonomousOperationEpoch_ = operationEpoch;
  lastCompletedAutonomousCompartment_ = 0;
  ++revision_;
  return true;
}

bool DeviceControl::CompleteAutonomousPresentation(uint8_t compartment) {
  if (autonomousCompartment_ == 0 ||
      autonomousCompartment_ != compartment ||
      autonomousOperationEpoch_ != OperationEpoch() ||
      (autonomousOperationEpoch_ != 0 &&
       FindOperation(autonomousOperationId_, compartment, autonomousFingerprint_,
                     autonomousOperationEpoch_) != CommandJournal::Lookup::Same) ||
      selection_.NeedsRecovery() || !journal_.HasPending() ||
      journal_.PendingCompartment() != compartment) {
    return false;
  }
  // The wand returned to rest, so its availability is retained atomically.
  if (!journal_.ResolveRestored(compartment)) {
    FaultAutonomousPresentation();
    return false;
  }
  autonomousCompartment_ = 0;
  lastCompletedAutonomousCompartment_ = compartment;
  ++revision_;
  return true;
}

bool DeviceControl::ConfirmTimedPresentation() {
  if (lastCompletedAutonomousCompartment_ == 0) return false;
  const uint8_t compartment = lastCompletedAutonomousCompartment_;
  lastCompletedAutonomousCompartment_ = 0;
  if (autonomousOperationEpoch_ != OperationEpoch()) return false;
  return autonomousOperationEpoch_ == 0
      ? journal_.ConfirmTimedPresentation(autonomousOperationId_, compartment, autonomousFingerprint_)
      : journal_.ConfirmTimedPresentationScoped(autonomousOperationEpoch_, autonomousOperationId_,
                                                 compartment, autonomousFingerprint_);
}

void DeviceControl::FaultAutonomousPresentation() {
  if (autonomousCompartment_ == 0) {
    return;
  }
  SelectionStep step;
  selection_.ReportFault(step);
  autonomousCompartment_ = 0;
  ++revision_;
  // Leave the journal open until physical inspection resolves the outcome.
}

bool DeviceControl::Tick(uint64_t nowMs, SelectionStep& step) {
  if (!manualLeaseId_.empty() && nowMs >= leaseExpiresAtMs_) {
    return LoseManualControl(step);
  }

  if (!executionStarted_) {
    if (selection_.ObserveControlLoopPass(static_cast<uint32_t>(nowMs),
                                          step)) {
      FinishCurrent(CommandOutcome::OutcomeUnknown);
      return true;
    }
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
  const uint64_t previousRevision = revision_;
  const bool newlyStopped = selection_.Stop(step);
  FaultAutonomousPresentation();
  if (HasPendingSelection()) {
    FinishCurrent(CommandOutcome::OutcomeUnknown);
  }
  if (newlyStopped && revision_ == previousRevision) {
    ++revision_;
  }

  return newlyStopped;
}

uint64_t DeviceControl::Revision() const { return revision_; }

CommandJournal::Lookup DeviceControl::FindOperation(
    const std::string& id, uint8_t slot, uint64_t fingerprint,
    uint64_t operationEpoch) const {
  if (operationEpoch != OperationEpoch()) return CommandJournal::Lookup::StaleEpoch;
  return operationEpoch == 0 ? journal_.Find(id, slot, fingerprint)
      : journal_.FindScoped(operationEpoch, id, slot, fingerprint);
}

bool DeviceControl::WasTimedPresentationCompleted(
    const std::string& id, uint8_t slot, uint64_t operationEpoch,
    uint64_t fingerprint) const {
  if (operationEpoch != 0 && fingerprint == 0) return false;
  return journal_.WasTimedPresentationCompletedHistorical(
      operationEpoch, id, slot, fingerprint);
}

uint64_t DeviceControl::CalibrationRevision() const {
  return calibrationRevision_;
}

const std::string& DeviceControl::PendingCommandId() const {
  return pendingCommandId_;
}

const LastSelectionResult& DeviceControl::LastResult() const { return lastResult_; }

uint8_t DeviceControl::ReadyMask() const { return readyMask_; }

bool DeviceControl::NeedsRecovery() const {
  return selection_.NeedsRecovery() || journal_.HasPending();
}

bool DeviceControl::HasPendingSelection() const {
  return !pendingCommandId_.empty();
}

bool DeviceControl::HasManualControl() const {
  return !manualLeaseId_.empty();
}

bool DeviceControl::HasAutonomousPresentation() const {
  return autonomousCompartment_ != 0;
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
    uint64_t operationEpoch, const std::string& commandId) {
  for (CachedResult& result : recentResults_) {
    if (result.occupied &&
        ((result.kind == ControlCommandKind::Selection &&
          result.selectionCommand.operationEpoch == operationEpoch &&
          result.selectionCommand.commandId == commandId) ||
         (result.kind == ControlCommandKind::InventoryReload &&
          result.reloadCommand.operationEpoch == operationEpoch &&
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

  if (outcome != CommandOutcome::Completed || !selection_.ReturnsProp())
    readyMask_ &= static_cast<uint8_t>(~(1u << (pendingCompartment_ - 1)));
  // An interrupted release has an unknown physical result. Keep its durable
  // record until an operator inspects the compartment after Stop or reboot.
  if (outcome == CommandOutcome::Completed &&
      !(selection_.ReturnsProp() ? journal_.ResolveRestored(pendingCompartment_) : journal_.Resolve(readyMask_))) {
    SelectionStep step;
    selection_.ReportFault(step);
    outcome = CommandOutcome::OutcomeUnknown;
  }

  CachedResult* result = FindCached(pendingOperationEpoch_, pendingCommandId_);
  if (result != nullptr) {
    result->receipt.outcome = outcome;
  }

  lastResult_.commandId = pendingCommandId_;
  lastResult_.compartment = pendingCompartment_;
  lastResult_.outcome = outcome;
  lastResult_.revision = revision_ + 1;

  pendingCommandId_.clear();
  pendingCompartment_ = 0;
  pendingOperationEpoch_ = 0;
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

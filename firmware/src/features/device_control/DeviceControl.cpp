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

void DeviceControl::PollStorage() {
  journal_.PollStorage();
  uint8_t durableMask = journal_.ReadyMask();
  if (journal_.HasPending() && !HasPendingSelection() && !HasAutonomousPresentation() &&
      deferredKind_ != DeferredKind::Selection) {
    const uint8_t compartment = journal_.PendingCompartment();
    if (compartment >= 1 && compartment <= SelectionSequence::CompartmentCount)
      durableMask &= static_cast<uint8_t>(~(1u << (compartment - 1)));
  }
  // Polling adopts late writes even when their actuation permission was cancelled.
  // A current reload/close owns its revision change when it consumes the result.
  if (readyMask_ != durableMask && deferredKind_ != DeferredKind::Reload &&
      !closingSelection_ && !recoveryInProgress_) {
    readyMask_ = durableMask;
    ++revision_;
  }
}

void DeviceControl::CancelDeferredCommand() {
  if (deferredKind_ == DeferredKind::None) return;
  journal_.CancelStorageAdmissions();
  SelectionStep step;
  selection_.ReportFault(step);
  ++revision_;
  deferredKind_ = DeferredKind::None;
  deferredSelection_ = SelectionCommand();
  deferredReload_ = InventoryReloadCommand();
  deferredReadyMask_ = 0;
}

CommandReceipt DeviceControl::RejectDeferred(CommandError error) {
  const CommandReceipt rejected(error);
  if (deferredKind_ == DeferredKind::Selection) Remember(deferredSelection_, rejected);
  if (deferredKind_ == DeferredKind::Reload) Remember(deferredReload_, rejected);
  CancelDeferredCommand();
  return rejected;
}

bool DeviceControl::RecoverAfterInspection() {
  if (selection_.IsBusy() || HasPendingSelection() || HasAutonomousPresentation()) return false;
  if (!recoveryInProgress_) {
    CancelDeferredCommand();
    CancelAutonomousReservation();
    journal_.BeginStorageRecovery();
    recoveryInProgress_ = true;
  }
  PollStorage();
  // Consume the exact recovery write even if polling already adopted its closed snapshot.
  if (recoveryResolvePending_ || journal_.HasPending()) {
    if (!recoveryResolvePending_) {
      const uint8_t compartment = journal_.PendingCompartment();
      recoveryReadyMask_ = compartment >= 1 && compartment <= SelectionSequence::CompartmentCount
          ? static_cast<uint8_t>(journal_.ReadyMask() & ~(1u << (compartment - 1)))
          : journal_.ReadyMask();
    }
    if (!journal_.Resolve(recoveryReadyMask_)) {
      recoveryResolvePending_ = journal_.LastStorageState() == CommandJournal::StorageState::Pending;
      return false;
    }
    recoveryResolvePending_ = false;
  }
  if (journal_.HasPendingStorage() || !journal_.ResumeStorageAfterRecovery()) return false;
  if (!selection_.RecoverAfterInspection()) return false;
  readyMask_ = journal_.ReadyMask();
  recoveryInProgress_ = false;
  ++revision_;
  return true;
}

bool DeviceControl::AcquireManualControl(const std::string& leaseId,
                                          uint64_t nowMs) {
  if (leaseId.empty() || !manualLeaseId_.empty() ||
      HasAutonomousPresentation() || selection_.IsBusy() ||
      selection_.NeedsRecovery() || journal_.HasPending() || HasStorageWork() || HasAutonomousReservation()) {
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
  if (!OwnsLiveManualLease(leaseId, nowMs) || NeedsRecovery() || HasStorageWork() ||
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
  PollStorage();
  const bool retry = deferredKind_ == DeferredKind::Selection && deferredSelection_.SameContentAs(command);
  if (deferredKind_ != DeferredKind::None && !retry) {
    const std::string& id = deferredKind_ == DeferredKind::Selection
        ? deferredSelection_.commandId : deferredReload_.commandId;
    return CommandReceipt(id == command.commandId ? CommandError::IdConflict : CommandError::Busy);
  }
  const auto reject = [&](CommandError error) {
    return retry ? RejectDeferred(error) : CommandReceipt(error);
  };
  if (!authenticated || command.deviceId != deviceId_) {
    return reject(CommandError::Unauthorized);
  }

  if (command.bootId != bootId_) {
    return reject(CommandError::WrongBoot);
  }

  if (command.operationEpoch != OperationEpoch()) {
    return reject(CommandError::WrongOperationEpoch);
  }

  if (manualLeaseId_.empty() || command.controlLeaseId != manualLeaseId_) {
    return reject(CommandError::LeaseExpired);
  }

  if (nowMs >= leaseExpiresAtMs_) {
    SelectionStep step;
    LoseManualControl(step);
    return reject(CommandError::LeaseExpired);
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

  if (!retry && command.sequence <= lastSequence_) {
    return {CommandError::AlreadySeenResultUnavailable,
            CommandOutcome::Rejected, false};
  }

  if (!retry && command.sequence != lastSequence_ + 1) {
    return {CommandError::SequenceGap, CommandOutcome::Rejected, false};
  }

  if (!retry) lastSequence_ = command.sequence;

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

  if (!retry && HasStorageWork()) return Reject(command, CommandError::Busy, true);
  const uint64_t fingerprint = command.operationEpoch == 0 ? 0 : SelectionFingerprint(command);
  if (!retry && command.operationEpoch != 0) {
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
    if (journal_.LastStorageState() == CommandJournal::StorageState::Pending) {
      if (!retry) {
        deferredKind_ = DeferredKind::Selection;
        deferredSelection_ = command;
      }
      return {CommandError::None, CommandOutcome::Rejected, false, true};
    }
    if (retry) return RejectDeferred(CommandError::JournalFailed);
    return Reject(command, CommandError::JournalFailed, true);
  }
  deferredKind_ = DeferredKind::None;
  deferredSelection_ = SelectionCommand();

  pendingCommandId_ = command.commandId;
  pendingCompartment_ = command.compartment;
  pendingOperationEpoch_ = command.operationEpoch;
  pendingIssuedAtMs_ = command.issuedAtDeviceMs;
  pendingMaxAgeMs_ = command.maxAgeMs;
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
  PollStorage();
  const bool retry = deferredKind_ == DeferredKind::Reload && deferredReload_.SameContentAs(command);
  if (deferredKind_ != DeferredKind::None && !retry) {
    const std::string& id = deferredKind_ == DeferredKind::Selection
        ? deferredSelection_.commandId : deferredReload_.commandId;
    return CommandReceipt(id == command.commandId ? CommandError::IdConflict : CommandError::Busy);
  }
  const auto rejectEnvelope = [&](CommandError error) {
    return retry ? RejectDeferred(error) : CommandReceipt(error);
  };
  if (!authenticated || command.deviceId != deviceId_) {
    return rejectEnvelope(CommandError::Unauthorized);
  }
  if (command.bootId != bootId_) {
    return rejectEnvelope(CommandError::WrongBoot);
  }
  if (command.operationEpoch != OperationEpoch()) {
    return rejectEnvelope(CommandError::WrongOperationEpoch);
  }
  if (manualLeaseId_.empty() || command.controlLeaseId != manualLeaseId_) {
    return rejectEnvelope(CommandError::LeaseExpired);
  }
  if (nowMs >= leaseExpiresAtMs_) {
    SelectionStep step;
    LoseManualControl(step);
    return rejectEnvelope(CommandError::LeaseExpired);
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
  if (!retry && command.sequence <= lastSequence_) {
    return {CommandError::AlreadySeenResultUnavailable,
            CommandOutcome::Rejected, false};
  }
  if (!retry && command.sequence != lastSequence_ + 1) {
    return {CommandError::SequenceGap, CommandOutcome::Rejected, false};
  }
  if (!retry) lastSequence_ = command.sequence;

  const auto reject = [&](CommandError error) {
    if (retry) return RejectDeferred(error);
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

  if (!retry && HasStorageWork()) return reject(CommandError::Busy);
  const uint8_t nextMask = static_cast<uint8_t>(
      readyMask_ | (1u << (command.compartment - 1)));
  if (retry || nextMask != readyMask_) {
    const uint8_t persistedMask = retry ? deferredReadyMask_ : nextMask;
    if (!journal_.PersistReadyMask(persistedMask)) {
      if (journal_.LastStorageState() == CommandJournal::StorageState::Pending) {
        if (!retry) {
          deferredKind_ = DeferredKind::Reload;
          deferredReload_ = command;
          deferredReadyMask_ = nextMask;
        }
        return {CommandError::None, CommandOutcome::Rejected, false, true};
      }
      return reject(CommandError::JournalFailed);
    }
    readyMask_ = journal_.ReadyMask();
    deferredKind_ = DeferredKind::None;
    deferredReload_ = InventoryReloadCommand();
    deferredReadyMask_ = 0;
    ++revision_;
  }

  const CommandReceipt accepted = {CommandError::None,
                                   CommandOutcome::Accepted, false};
  Remember(command, accepted);
  return accepted;
}

bool DeviceControl::BeginAcceptedSelection(uint64_t nowMs,
                                            SelectionStep& step) {
  if (!HasPendingSelection() || executionStarted_ || closingSelection_ ||
      pendingOperationEpoch_ != OperationEpoch()) {
    return false;
  }

  if (manualLeaseId_.empty() || nowMs >= leaseExpiresAtMs_) {
    LoseManualControl(step);
    return false;
  }

  // Sending the final ACK may itself take time. Its success cannot extend the
  // original envelope's motion admission window, even while the lease is live.
  if (pendingIssuedAtMs_ > nowMs + 500 ||
      (nowMs >= pendingIssuedAtMs_ && nowMs - pendingIssuedAtMs_ > pendingMaxAgeMs_)) {
    journal_.CancelStorageAdmissions();
    selection_.ReportFault(step);
    FinishCurrent(CommandOutcome::OutcomeUnknown);
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

bool DeviceControl::CanAdmitAutonomousPresentation(
    const std::string& proposalId, uint8_t compartment,
    uint64_t expectedRevision, uint64_t fingerprint, uint64_t operationEpoch) const {
  return operationEpoch == OperationEpoch() && (operationEpoch == 0 || fingerprint != 0) &&
      !proposalId.empty() && compartment >= 1 && compartment <= SelectionSequence::CompartmentCount &&
      expectedRevision == revision_ && calibrationRevision_ != 0 &&
      !HasManualControl() && !HasPendingSelection() && !HasAutonomousPresentation() &&
      !selection_.IsBusy() && !selection_.NeedsRecovery() && !journal_.HasPending() && !HasStorageWork() &&
      (readyMask_ & (1u << (compartment - 1))) != 0;
}

bool DeviceControl::ReserveAutonomousPresentation(
    const std::string& proposalId, uint8_t compartment,
    uint64_t expectedRevision, uint64_t fingerprint, uint64_t operationEpoch) {
  return operationEpoch != 0 && CanAdmitAutonomousPresentation(proposalId, compartment,
      expectedRevision, fingerprint, operationEpoch) &&
      journal_.ReservePresentation(operationEpoch, proposalId, compartment, fingerprint);
}

bool DeviceControl::BeginAutonomousPresentation(
    const std::string& proposalId, uint8_t compartment,
    uint64_t expectedRevision, uint64_t fingerprint, uint64_t operationEpoch) {
  if (!CanAdmitAutonomousPresentation(proposalId, compartment,
      expectedRevision, fingerprint, operationEpoch)) return false;

  // The durable record must exist before a servo port may request motion.
  if (operationEpoch != 0 &&
      !HasAutonomousReservation(proposalId, compartment, fingerprint, operationEpoch)) return false;
  const bool recorded = operationEpoch == 0
      ? journal_.BeginWithIdentity(proposalId, compartment, fingerprint)
      : journal_.BeginReservedPresentation(operationEpoch, proposalId, compartment, fingerprint);
  if (!recorded) {
    journal_.CancelStorageAdmissions();
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
  CancelAutonomousReservation();
  if (autonomousCompartment_ == 0) {
    return;
  }
  journal_.CancelStorageAdmissions();
  SelectionStep step;
  selection_.ReportFault(step);
  autonomousCompartment_ = 0;
  ++revision_;
  // Leave the journal open until physical inspection resolves the outcome.
}

bool DeviceControl::Tick(uint64_t nowMs, SelectionStep& step) {
  PollStorage();
  if (journal_.HasStorageFault() && !selection_.NeedsRecovery()) return Stop(step);
  if (deferredKind_ != DeferredKind::None) {
    const uint64_t issuedAt = deferredKind_ == DeferredKind::Selection
        ? deferredSelection_.issuedAtDeviceMs : deferredReload_.issuedAtDeviceMs;
    const uint32_t maxAge = deferredKind_ == DeferredKind::Selection
        ? deferredSelection_.maxAgeMs : deferredReload_.maxAgeMs;
    if (issuedAt > nowMs + 500 || nowMs > issuedAt + maxAge)
      RejectDeferred(CommandError::Expired);
    else if (selection_.NeedsRecovery())
      RejectDeferred(CommandError::SafetyLatched);
  }
  if (!manualLeaseId_.empty() && nowMs >= leaseExpiresAtMs_) {
    return LoseManualControl(step);
  }

  const uint32_t sequenceTimeMs = static_cast<uint32_t>(nowMs);
  if (selection_.ObserveControlLoopPass(sequenceTimeMs, step)) {
    journal_.CancelStorageAdmissions();
    CancelDeferredCommand();
    recoveryInProgress_ = false;
    recoveryResolvePending_ = false;
    FinishCurrent(CommandOutcome::OutcomeUnknown);
    return true;
  }
  if (closingSelection_) {
    const uint8_t compartment = pendingCompartment_;
    if (selection_.NeedsRecovery()) {
      journal_.CancelStorageAdmissions();
      FinishCurrent(CommandOutcome::OutcomeUnknown);
      step = {SelectionEffect::Stopped, compartment};
      return true;
    }
    const CommandOutcome outcome = FinishCurrent(CommandOutcome::Completed);
    if (outcome == CommandOutcome::Accepted) return false;
    step = {outcome == CommandOutcome::Completed ? SelectionEffect::Idle : SelectionEffect::Stopped,
            compartment};
    return true;
  }
  if (!executionStarted_ || !selection_.Update(sequenceTimeMs, step)) return false;

  if (step.effect == SelectionEffect::Idle) {
    const uint8_t compartment = pendingCompartment_;
    if (FinishCurrent(CommandOutcome::Completed) == CommandOutcome::OutcomeUnknown)
      step = {SelectionEffect::Stopped, compartment};
  } else if (step.effect == SelectionEffect::Stopped) {
    journal_.CancelStorageAdmissions();
    FinishCurrent(CommandOutcome::OutcomeUnknown);
  }
  return true;
}

bool DeviceControl::Stop(SelectionStep& step) {
  // Stop invalidates even idle or storage-only admissions without waiting for NVS.
  journal_.CancelStorageAdmissions();
  CancelDeferredCommand();
  recoveryInProgress_ = false;
  recoveryResolvePending_ = false;
  lastCompletedAutonomousCompartment_ = 0;
  CancelAutonomousReservation();
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
  return selection_.NeedsRecovery() || journal_.HasPending() || recoveryInProgress_;
}

bool DeviceControl::HasStorageWork() const {
  return deferredKind_ != DeferredKind::None || closingSelection_ ||
      recoveryInProgress_ || journal_.HasPendingStorage();
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
  if (deferredKind_ == DeferredKind::Selection && deferredSelection_.SameContentAs(command))
    CancelDeferredCommand();

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
  if (!HasPendingSelection()) return outcome;

  if (outcome == CommandOutcome::Completed) {
    const uint8_t completedMask = selection_.ReturnsProp() ? readyMask_
        : static_cast<uint8_t>(readyMask_ & ~(1u << (pendingCompartment_ - 1)));
    const bool recorded = selection_.ReturnsProp()
        ? journal_.ResolveRestored(pendingCompartment_) : journal_.Resolve(completedMask);
    if (!recorded && journal_.LastStorageState() == CommandJournal::StorageState::Pending) {
      closingSelection_ = true;
      executionStarted_ = false;
      // Physical rest is immediate; reporting completion waits for durable acknowledgement.
      return CommandOutcome::Accepted;
    }
    if (!recorded) {
      SelectionStep step;
      selection_.ReportFault(step);
      outcome = CommandOutcome::OutcomeUnknown;
    } else {
      readyMask_ = journal_.ReadyMask();
    }
  }
  if (outcome != CommandOutcome::Completed)
    readyMask_ &= static_cast<uint8_t>(~(1u << (pendingCompartment_ - 1)));

  CachedResult* result = FindCached(pendingOperationEpoch_, pendingCommandId_);
  if (result != nullptr) result->receipt.outcome = outcome;

  lastResult_.commandId = pendingCommandId_;
  lastResult_.compartment = pendingCompartment_;
  lastResult_.outcome = outcome;
  lastResult_.revision = revision_ + 1;
  pendingCommandId_.clear();
  pendingCompartment_ = 0;
  pendingOperationEpoch_ = 0;
  pendingIssuedAtMs_ = 0;
  pendingMaxAgeMs_ = 0;
  executionStarted_ = false;
  closingSelection_ = false;
  ++revision_;
  return outcome;
}

bool DeviceControl::LoseManualControl(SelectionStep& step) {
  if (manualLeaseId_.empty()) {
    return false;
  }

  journal_.CancelStorageAdmissions();
  CancelDeferredCommand();
  recoveryInProgress_ = false;
  recoveryResolvePending_ = false;
  const bool newlyStopped = selection_.Stop(step);
  if (HasPendingSelection()) {
    FinishCurrent(CommandOutcome::OutcomeUnknown);
  }

  manualLeaseId_.clear();
  leaseExpiresAtMs_ = 0;
  ++revision_;
  return newlyStopped;
}

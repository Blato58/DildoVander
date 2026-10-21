#pragma once

#include <stdint.h>
#include <stddef.h>

#include <array>
#include <string>

#include "../wand_selection/SelectionSequence.h"
#include "CommandJournal.h"

enum class SelectionSource : uint8_t {
  Manual,
  OperatorConfirmedAi,
  OperatorCorrectedAi,
  ButtonConfirmedAi,
};

enum class CommandError : uint8_t {
  None,
  InvalidMessage,
  Unauthorized,
  WrongBoot,
  LeaseExpired,
  Expired,
  StaleRevision,
  SequenceGap,
  AlreadySeenResultUnavailable,
  IdConflict,
  Busy,
  CompartmentUnavailable,
  NotCalibrated,
  SafetyLatched,
  JournalFailed,
  WrongOperationEpoch,
};

enum class ControlCommandKind : uint8_t {
  Selection,
  InventoryReload,
};

enum class CommandOutcome : uint8_t {
  Rejected,
  Accepted,
  Completed,
  OutcomeUnknown,
};

struct SelectionCommand {
  std::string commandId;
  std::string deviceId;
  std::string bootId;
  std::string controlLeaseId;
  uint64_t sequence = 0;
  uint64_t expectedRevision = 0;
  uint64_t issuedAtDeviceMs = 0;
  uint32_t maxAgeMs = 0;
  uint8_t compartment = 0;
  SelectionSource source = SelectionSource::Manual;
  uint64_t operationEpoch = 0;

  bool SameContentAs(const SelectionCommand& other) const;
};

struct InventoryReloadCommand {
  std::string commandId;
  std::string deviceId;
  std::string bootId;
  std::string controlLeaseId;
  uint64_t sequence = 0;
  uint64_t expectedRevision = 0;
  uint64_t issuedAtDeviceMs = 0;
  uint32_t maxAgeMs = 0;
  uint8_t compartment = 0;
  bool inspectionConfirmed = false;
  uint64_t operationEpoch = 0;

  bool SameContentAs(const InventoryReloadCommand& other) const;
};

struct CommandReceipt {
  CommandReceipt(CommandError error = CommandError::None,
                 CommandOutcome outcome = CommandOutcome::Rejected,
                 bool duplicate = false, bool deferred = false)
      : error(error), outcome(outcome), duplicate(duplicate), deferred(deferred) {}

  CommandError error;
  CommandOutcome outcome;
  bool duplicate;
  bool deferred;
};

struct LastSelectionResult {
  std::string commandId;
  uint8_t compartment = 0;
  CommandOutcome outcome = CommandOutcome::Rejected;
  uint64_t revision = 0;
};

class DeviceControl {
 public:
  enum class AutonomousAdmissionProgress { Rejected, Pending, Succeeded };
  static constexpr uint64_t ManualLeaseDurationMs = 6000;
  static constexpr size_t ResultCacheSize = 64;

  DeviceControl(const std::string& deviceId, const std::string& bootId,
                SelectionSequence& sequence, CommandJournal& journal,
                uint64_t calibrationRevision);

  bool RecoverAfterInspection();
  using RecoveryProgress = InspectedRecoveryProgress;
  RecoveryProgress BeginInspectedRecovery();
  RecoveryProgress PollInspectedRecovery();
  void CancelInspectedRecovery();
  bool HasRecoveryWork() const { return recoveryInProgress_ || journal_.HasRecoveryStorageWork(); }
  bool AcquireManualControl(const std::string& leaseId, uint64_t nowMs);
  bool RenewManualLease(const std::string& leaseId, uint64_t nowMs);
  bool DisconnectManualControl(SelectionStep& step);
  bool ReleaseIdleManualControl(const std::string& leaseId, uint64_t nowMs);

  CommandReceipt AcceptSelection(const SelectionCommand& command,
                                 bool authenticated, uint64_t nowMs);
  CommandReceipt ConfirmReload(const InventoryReloadCommand& command,
                               bool authenticated, uint64_t nowMs);
  bool BeginAcceptedSelection(uint64_t nowMs, SelectionStep& step);
  bool ReserveAutonomousPresentation(const std::string& proposalId, uint8_t compartment,
      uint64_t expectedRevision, uint64_t fingerprint, uint64_t operationEpoch, bool technicalTest = false);
  AutonomousAdmissionProgress LastAutonomousAdmissionProgress() const {
    return autonomousAdmissionProgress_;
  }
  bool HasPendingAutonomousAdmission(const std::string& proposalId, uint8_t compartment,
      uint64_t expectedRevision, uint64_t fingerprint, uint64_t operationEpoch) const;
  bool HasAutonomousReservation() const { return journal_.HasPresentationReservation(); }
  bool HasAutonomousReservation(const std::string& id, uint8_t slot,
      uint64_t fingerprint, uint64_t epoch) const {
    return journal_.HasPresentationReservation(epoch, id, slot, fingerprint);
  }
  void CancelAutonomousReservation();
  bool BeginAutonomousPresentation(const std::string& proposalId,
                                   uint8_t compartment,
                                   uint64_t expectedRevision, uint64_t fingerprint = 0,
                                   uint64_t operationEpoch = 0);
  bool CompleteAutonomousPresentation(uint8_t compartment);
  bool ConfirmTimedPresentation();
  bool IsAutonomousCompletionStoragePending() const {
    return journal_.LastStorageState() == CommandJournal::StorageState::Pending;
  }
  bool HasAutonomousCompletionFault() const {
    // Its own open presentation journal is expected while waiting for close.
    return selection_.NeedsRecovery() || recoveryInProgress_ || journal_.HasStorageFault();
  }
  void FaultAutonomousPresentation();
  bool Tick(uint64_t nowMs, SelectionStep& step);
  bool Stop(SelectionStep& step);

  uint64_t Revision() const;
  uint64_t CalibrationRevision() const;
  uint64_t OperationEpoch() const { return journal_.CurrentEpoch(); }
  const std::string& PendingCommandId() const;
  const LastSelectionResult& LastResult() const;
  uint8_t ReadyMask() const;
  bool NeedsRecovery() const;
  bool HasPendingSelection() const;
  bool HasStorageWork() const;
  bool HasManualControl() const;
  bool OwnsManualLease(const std::string& leaseId) const;
  bool OwnsLiveManualLease(const std::string& leaseId, uint64_t nowMs) const;
  bool HasAutonomousPresentation() const;
  bool CanRecordOperation() const { return !HasStorageWork() && journal_.CanBeginOperation(); }
  bool WasTimedPresentationCompleted(const std::string& id, uint8_t slot) const {
    return journal_.WasTimedPresentationCompleted(id, slot);
  }
  bool WasLegacyTimedPresentationCompleted(const std::string& id, uint8_t slot) const {
    return journal_.WasLegacyTimedPresentationCompleted(id, slot);
  }
  bool WasTimedPresentationCompleted(const std::string& id, uint8_t slot,
                                    uint64_t operationEpoch,
                                    uint64_t fingerprint) const;
  CommandJournal::Lookup FindOperation(const std::string& id, uint8_t slot,
                                      uint64_t fingerprint,
                                      uint64_t operationEpoch = 0) const;

 private:
  bool CanAdmitAutonomousPresentation(const std::string& proposalId, uint8_t compartment,
      uint64_t expectedRevision, uint64_t fingerprint, uint64_t operationEpoch,
      bool ownContinuation = false) const;
  enum class AutonomousAdmissionKind { None, Reserve, Begin };
  struct DeferredAutonomousAdmission {
    AutonomousAdmissionKind kind = AutonomousAdmissionKind::None;
    std::string proposalId;
    uint8_t compartment = 0;
    uint64_t expectedRevision = 0, fingerprint = 0, operationEpoch = 0;
    uint64_t calibrationRevision = 0;
    bool technicalTest = false;
  } deferredAutonomousAdmission_;
  AutonomousAdmissionProgress autonomousAdmissionProgress_ = AutonomousAdmissionProgress::Rejected;
  bool AdvanceAutonomousAdmission(AutonomousAdmissionKind kind,
      const std::string& proposalId, uint8_t compartment, uint64_t expectedRevision,
      uint64_t fingerprint, uint64_t operationEpoch, bool technicalTest = false);
  void CancelDeferredAutonomousAdmission(bool reportFault = true);
  struct CachedResult {
    bool occupied = false;
    ControlCommandKind kind = ControlCommandKind::Selection;
    SelectionCommand selectionCommand;
    InventoryReloadCommand reloadCommand;
    CommandReceipt receipt;
  };

  CommandReceipt Reject(const SelectionCommand& command, CommandError error,
                        bool consumeSequence);
  void Remember(const SelectionCommand& command, CommandReceipt receipt);
  void Remember(const InventoryReloadCommand& command, CommandReceipt receipt);
  CachedResult* FindCached(uint64_t operationEpoch, const std::string& commandId);
  CommandOutcome FinishCurrent(CommandOutcome outcome);
  bool LoseManualControl(SelectionStep& step);
  void PollStorage();
  void CancelDeferredCommand();
  CommandReceipt RejectDeferred(CommandError error);

  enum class DeferredKind : uint8_t { None, Selection, Reload };
  DeferredKind deferredKind_ = DeferredKind::None;
  SelectionCommand deferredSelection_;
  InventoryReloadCommand deferredReload_;
  uint8_t deferredReadyMask_ = 0;
  bool closingSelection_ = false;
  bool recoveryInProgress_ = false;
  bool recoveryResolvePending_ = false;
  RecoveryProgress recoveryProgress_ = RecoveryProgress::Rejected;
  RecoveryProgress FinishRecovery(RecoveryProgress progress);
  uint8_t recoveryReadyMask_ = 0;

  std::string deviceId_;
  std::string bootId_;
  SelectionSequence& selection_;
  CommandJournal& journal_;
  uint8_t readyMask_;
  uint64_t calibrationRevision_;
  uint64_t revision_ = 0;
  uint64_t lastSequence_ = 0;
  uint64_t leaseExpiresAtMs_ = 0;
  std::string manualLeaseId_;
  std::array<CachedResult, ResultCacheSize> recentResults_;
  size_t nextResultIndex_ = 0;
  std::string pendingCommandId_;
  uint8_t pendingCompartment_ = 0;
  uint64_t pendingOperationEpoch_ = 0;
  uint64_t pendingIssuedAtMs_ = 0;
  uint32_t pendingMaxAgeMs_ = 0;
  bool executionStarted_ = false;
  uint8_t autonomousCompartment_ = 0;
  std::string autonomousOperationId_;
  uint64_t autonomousFingerprint_ = 0;
  uint64_t autonomousOperationEpoch_ = 0;
  uint8_t lastCompletedAutonomousCompartment_ = 0;
  bool closingAutonomousPresentation_ = false;
  LastSelectionResult lastResult_;
};

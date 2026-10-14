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

  bool SameContentAs(const InventoryReloadCommand& other) const;
};

struct CommandReceipt {
  CommandReceipt(CommandError error = CommandError::None,
                 CommandOutcome outcome = CommandOutcome::Rejected,
                 bool duplicate = false)
      : error(error), outcome(outcome), duplicate(duplicate) {}

  CommandError error;
  CommandOutcome outcome;
  bool duplicate;
};

struct LastSelectionResult {
  std::string commandId;
  uint8_t compartment = 0;
  CommandOutcome outcome = CommandOutcome::Rejected;
  uint64_t revision = 0;
};

class DeviceControl {
 public:
  static constexpr uint64_t ManualLeaseDurationMs = 6000;
  static constexpr size_t ResultCacheSize = 64;

  DeviceControl(const std::string& deviceId, const std::string& bootId,
                SelectionSequence& sequence, CommandJournal& journal,
                uint64_t calibrationRevision);

  bool RecoverAfterInspection();
  bool AcquireManualControl(const std::string& leaseId, uint64_t nowMs);
  bool RenewManualLease(const std::string& leaseId, uint64_t nowMs);
  bool DisconnectManualControl(SelectionStep& step);

  CommandReceipt AcceptSelection(const SelectionCommand& command,
                                 bool authenticated, uint64_t nowMs);
  CommandReceipt ConfirmReload(const InventoryReloadCommand& command,
                               bool authenticated, uint64_t nowMs);
  bool BeginAcceptedSelection(uint64_t nowMs, SelectionStep& step);
  bool BeginAutonomousPresentation(const std::string& proposalId,
                                   uint8_t compartment,
                                   uint64_t expectedRevision, uint64_t fingerprint = 0);
  bool CompleteAutonomousPresentation(uint8_t compartment);
  void FaultAutonomousPresentation();
  bool Tick(uint64_t nowMs, SelectionStep& step);
  bool Stop(SelectionStep& step);

  uint64_t Revision() const;
  uint64_t CalibrationRevision() const;
  const std::string& PendingCommandId() const;
  const LastSelectionResult& LastResult() const;
  uint8_t ReadyMask() const;
  bool NeedsRecovery() const;
  bool HasPendingSelection() const;
  bool HasManualControl() const;
  bool OwnsManualLease(const std::string& leaseId) const;
  bool OwnsLiveManualLease(const std::string& leaseId, uint64_t nowMs) const;
  bool HasAutonomousPresentation() const;
  CommandJournal::Lookup FindOperation(const std::string& id, uint8_t slot,
                                      uint64_t fingerprint) const { return journal_.Find(id, slot, fingerprint); }

 private:
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
  CachedResult* FindCached(const std::string& commandId);
  CommandOutcome FinishCurrent(CommandOutcome outcome);
  bool LoseManualControl(SelectionStep& step);

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
  bool executionStarted_ = false;
  uint8_t autonomousCompartment_ = 0;
  LastSelectionResult lastResult_;
};

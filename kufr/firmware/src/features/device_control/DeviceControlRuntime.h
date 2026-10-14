#pragma once

#include <stdint.h>

#include <string>

#include "DeviceControl.h"
#include "../conversation/ConversationRuntimeState.h"

class WandPresentation;

// Sole control-task entry point for journal/control transitions and the
// coherent snapshot consumed by the backend network task. The runtime state
// must start unavailable; only this owner publishes it after safe transitions.
class DeviceControlRuntime final {
 public:
  DeviceControlRuntime(DeviceControl& control, SelectionSequence& selection,
                       ConversationRuntimeState& runtime,
                       const std::string& catalogVersion);

  bool BindPresentation(WandPresentation& presentation);

  bool RecoverAfterInspection();
  bool AcquireManualControl(const std::string& leaseId, uint64_t nowMs);
  bool RenewManualLease(const std::string& leaseId, uint64_t nowMs);
  bool DisconnectManualControl(SelectionStep& step);
  bool DisconnectManualControl(const std::string& leaseId,
                               SelectionStep& step);
  bool CancelConversation(const std::string& leaseId,
                          uint64_t expectedGeneration, uint64_t nowMs);
  bool OwnsLiveManualLease(const std::string& leaseId, uint64_t nowMs) const;
  CommandReceipt AcceptSelection(const SelectionCommand& command,
                                 bool authenticated, uint64_t nowMs);
  CommandReceipt ConfirmReload(const InventoryReloadCommand& command,
                               bool authenticated, uint64_t nowMs);
  bool BeginAcceptedSelection(uint64_t nowMs, SelectionStep& step);
  bool BeginAutonomousPresentation(
      const std::string& proposalId, uint8_t compartment,
      const ConversationRuntimeSnapshot& expectedSnapshot, uint64_t fingerprint = 0);
  bool CompleteAutonomousPresentation(uint8_t compartment,
      bool (*postPersistCheck)(void*) = nullptr, void* context = nullptr);
  void FaultAutonomousPresentation();
  bool Tick(uint64_t nowMs, SelectionStep& step);
  bool Stop(SelectionStep& step);

  bool PublishCurrent();
  void SetExternalActivity(bool active);
  uint64_t Generation() const;
  bool HasAutonomousPresentation() const;
  bool ReserveDecoration(uint64_t generation);
  bool ReleaseDecoration(uint64_t generation);
  bool HasDecoration() const { return decorationActive_; }
  CommandJournal::Lookup FindOperation(const std::string& id, uint8_t slot,
                                      uint64_t fingerprint) const { return control_.FindOperation(id, slot, fingerprint); }

 private:
  void RecordLeaseLoss(bool hadManualControl);

  DeviceControl& control_;
  SelectionSequence& selection_;
  ConversationRuntimeState& runtime_;
  std::string catalogVersion_;
  uint64_t generation_ = 0;
  uint64_t publishedGeneration_ = 0;
  uint64_t publishedRevision_ = 0;
  uint8_t publishedReadyMask_ = 0;
  bool published_ = false;
  bool externalActivity_ = false;
  bool decorationActive_ = false;
  uint64_t decorationGeneration_ = 0;
  WandPresentation* presentation_ = nullptr;
};

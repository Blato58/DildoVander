#pragma once

#include <stdint.h>

#include <string>

#include "DeviceControl.h"
#include "../conversation/ConversationRuntimeState.h"
#include "../conversation/PendingProposal.h"

class WandPresentation;
class PendingProposal;

// Sole control-task entry point for journal/control transitions and the
// coherent snapshot consumed by the backend network task. The runtime state
// must start unavailable; only this owner publishes it after safe transitions.
class DeviceControlRuntime final {
 public:
  enum class PresentationProgress { Pending, Succeeded, Rejected };
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
  bool ReserveAutonomousPresentation(const std::string& proposalId, uint8_t compartment,
      const ConversationRuntimeSnapshot& expectedSnapshot, uint64_t fingerprint);
  bool HasAutonomousReservation(const std::string& id, uint8_t slot,
      uint64_t fingerprint, uint64_t epoch) const {
    return control_.HasAutonomousReservation(id, slot, fingerprint, epoch);
  }
  void CancelAutonomousReservation() { control_.CancelAutonomousReservation(); }
  bool BeginAutonomousPresentation(
      const std::string& proposalId, uint8_t compartment,
      const ConversationRuntimeSnapshot& expectedSnapshot, uint64_t fingerprint = 0);
  PresentationProgress ReserveClaimedAutonomousPresentation(
      const PendingProposal::ClaimedProposal& claim, uint64_t fingerprint,
      uint64_t calibrationRevision, uint64_t nowMs);
  PresentationProgress BeginClaimedAutonomousPresentation(
      const PendingProposal::ClaimedProposal& claim, uint64_t fingerprint,
      uint64_t calibrationRevision, uint64_t nowMs);
  bool ValidateClaimedAutonomousPresentation(const PendingProposal::ClaimedProposal& claim,
      uint64_t fingerprint, uint64_t calibrationRevision, uint64_t nowMs) const;
  void CancelClaimedAutonomousPresentation();
  bool CompleteAutonomousPresentation(uint8_t compartment,
      bool (*postPersistCheck)(void*) = nullptr, void* context = nullptr);
  PresentationProgress AdvanceAutonomousCompletion(uint8_t compartment,
      bool (*postPersistCheck)(void*) = nullptr, void* context = nullptr);
  bool HasValidatedAutonomousCompletion() const;
  void FaultAutonomousPresentation();
  bool Tick(uint64_t nowMs, SelectionStep& step);
  bool Stop(SelectionStep& step);

  bool PublishCurrent();
  void SetExternalActivity(bool active);
  bool HasStorageWork() const {
    return admission_.owner != nullptr || offlinePreparation_.active || control_.HasStorageWork();
  }
  uint64_t Generation() const;
  uint64_t CurrentRevision() const { return control_.Revision(); }
  bool NeedsRecovery() const { return control_.NeedsRecovery(); }
  bool HasAutonomousPresentation() const;
  // Consulted only by the control owner immediately before new admission.
  bool CanCapturePresentationFingerprint() const;
  bool CanCaptureOwnedPresentationFingerprint() const;
  bool SetOfflineAuthority(bool active);
  bool HandoffToOffline(const std::string& leaseId, uint64_t nowMs);
  bool OfflineAuthority() const { return offlineAuthority_; }
  bool StartOfflinePresentation(PendingProposal& pending, uint64_t nowMs);
  bool StartOfflinePresentationPolled(PendingProposal& pending, uint64_t nowMs);
  PresentationProgress ReserveOfflinePreparation(const std::string& id, uint8_t slot,
      const ConversationRuntimeSnapshot& original, uint64_t fingerprint,
      uint64_t calibrationRevision, uint64_t requestedAtMs, uint64_t nowMs);
  void FinishOfflinePreparation();
  void CancelOfflinePreparation();
  bool ReserveDecoration(uint64_t generation, bool preserveConversationSnapshot = false);
  bool ReleaseDecoration(uint64_t generation);
  bool HasDecoration() const { return decorationActive_; }
  bool WasTimedPresentationCompleted(const std::string& id, uint8_t slot,
                                    uint64_t operationEpoch,
                                    uint64_t fingerprint) const {
    return control_.WasTimedPresentationCompleted(id, slot, operationEpoch, fingerprint);
  }
  CommandJournal::Lookup FindOperation(const std::string& id, uint8_t slot,
                                      uint64_t fingerprint,
                                      uint64_t operationEpoch = 0) const {
    return control_.FindOperation(id, slot, fingerprint, operationEpoch);
  }

 private:
  void RecordLeaseLoss(bool hadManualControl);
  struct OwnedAdmission {
    const PendingProposal::ClaimedProposal* owner = nullptr;
    std::string proposalId;
    uint64_t fingerprint = 0, calibration = 0, storedAt = 0, deadline = 0;
    uint64_t generation = 0, revision = 0, epoch = 0, snapshotVersion = 0;
    uint8_t slot = 0;
    bool offline = false;
  } admission_;
  struct OfflinePreparation {
    bool active = false;
    std::string id;
    uint8_t slot = 0;
    uint64_t generation = 0, revision = 0, epoch = 0, fingerprint = 0;
    uint64_t calibration = 0, requestedAt = 0, snapshotVersion = 0;
  } offlinePreparation_;
  bool CaptureClaimedAdmission(const PendingProposal::ClaimedProposal& claim,
      uint64_t fingerprint, uint64_t calibrationRevision, uint64_t nowMs);
  enum class CompletionPhase { None, Closing, Validating, Proof };
  struct AutonomousCompletion {
    CompletionPhase phase = CompletionPhase::None;
    uint64_t generation = 0;
    uint8_t compartment = 0;
    bool (*postPersistCheck)(void*) = nullptr;
    void* context = nullptr;
  } completion_;
  PresentationProgress RejectAutonomousCompletion();

  DeviceControl& control_;
  SelectionSequence& selection_;
  ConversationRuntimeState& runtime_;
  std::string catalogVersion_;
  uint64_t generation_ = 0;
  uint64_t publishedGeneration_ = 0;
  uint64_t publishedRevision_ = 0;
  uint64_t publishedOperationEpoch_ = 0;
  uint8_t publishedReadyMask_ = 0;
  bool published_ = false;
  bool externalActivity_ = false;
  bool offlineAuthority_ = false, offlineAdmission_ = false;
  bool decorationActive_ = false, decorationPreservesSnapshot_ = false;
  uint64_t decorationGeneration_ = 0;
  WandPresentation* presentation_ = nullptr;
};

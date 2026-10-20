#include "DeviceControlRuntime.h"

#include "../wand_presentation/WandPresentation.h"
#include <limits>

DeviceControlRuntime::DeviceControlRuntime(
    DeviceControl& control, SelectionSequence& selection,
    ConversationRuntimeState& runtime, const std::string& catalogVersion)
    : control_(control),
      selection_(selection),
      runtime_(runtime),
      catalogVersion_(catalogVersion) {}

bool DeviceControlRuntime::BindPresentation(
    WandPresentation& presentation) {
  if (presentation_ != nullptr ||
      presentation.State() != WandPresentationState::Idle) {
    return false;
  }
  presentation_ = &presentation;
  return true;
}

bool DeviceControlRuntime::RecoverAfterInspection() {
  // This compatibility entry point is an explicit inspection call. A previous
  // terminal attempt cannot consume that new call; typed handles keep their
  // terminal result until their actual owner explicitly finishes them.
  if (legacyRecovery_.IsBound() && recovery_.progress != RecoveryProgress::Pending)
    FinishInspectedRecovery(legacyRecovery_);
  const auto progress = legacyRecovery_.IsBound() ? PollInspectedRecovery(legacyRecovery_)
      : BeginInspectedRecovery(legacyRecovery_);
  if (progress != RecoveryProgress::Pending) FinishInspectedRecovery(legacyRecovery_);
  return progress == RecoveryProgress::Succeeded;
}
bool DeviceControlRuntime::OwnsRecovery(const RecoveryHandle& handle) const {
  return handle.owner_ == this && handle.serial_ && recovery_.owner == &handle && recovery_.serial == handle.serial_;
}
DeviceControlRuntime::RecoveryProgress DeviceControlRuntime::BeginInspectedRecovery(RecoveryHandle& handle) {
  if (handle.IsBound()) return OwnsRecovery(handle) ? PollInspectedRecovery(handle) : RecoveryProgress::Rejected;
  if (recovery_.owner || control_.HasRecoveryWork() || externalActivity_ || decorationActive_ || offlinePreparation_.active
      || nextRecoverySerial_ == std::numeric_limits<uint64_t>::max()) return RecoveryProgress::Rejected;
  if (presentation_ != nullptr &&
      presentation_->State() != WandPresentationState::Idle &&
      presentation_->State() != WandPresentationState::Fault) return RecoveryProgress::Rejected;
  handle.owner_ = this; handle.serial_ = ++nextRecoverySerial_;
  recovery_.owner = &handle; recovery_.serial = handle.serial_;
  recovery_.generation = generation_; recovery_.originalRevision = control_.Revision();
  recovery_.progress = RecoveryProgress::Pending;
  const auto progress = control_.BeginInspectedRecovery();
  // Initial cancellation of an older deferred command belongs to this attempt;
  // an external revision change after Begin revokes the retained capability.
  recovery_.revision = control_.Revision();
  return AdoptRecoveryProgress(progress);
}
DeviceControlRuntime::RecoveryProgress DeviceControlRuntime::AdoptRecoveryProgress(RecoveryProgress progress) {
  if (progress == RecoveryProgress::Succeeded) completion_ = AutonomousCompletion();
  if (progress == RecoveryProgress::Succeeded && presentation_ != nullptr &&
      presentation_->State() == WandPresentationState::Fault &&
      !presentation_->RecoverAfterInspection(true)) {
    SelectionStep ignored;
    Stop(ignored);
    progress = RecoveryProgress::Rejected;
  }
  recovery_.progress = progress;
  PublishCurrent();
  return progress;
}
DeviceControlRuntime::RecoveryProgress DeviceControlRuntime::PollInspectedRecovery(RecoveryHandle& handle) {
  if (!OwnsRecovery(handle)) return RecoveryProgress::Rejected;
  if (recovery_.progress != RecoveryProgress::Pending) return recovery_.progress;
  if (generation_ != recovery_.generation || control_.Revision() != recovery_.revision
      || externalActivity_ || decorationActive_) {
    CancelInspectedRecovery(handle);
    return recovery_.progress;
  }
  return AdoptRecoveryProgress(control_.PollInspectedRecovery());
}
void DeviceControlRuntime::CancelInspectedRecovery(RecoveryHandle& handle) {
  if (!OwnsRecovery(handle)) return;
  if (recovery_.progress != RecoveryProgress::Pending && recovery_.progress != RecoveryProgress::Succeeded) return;
  if (recovery_.generation == generation_) {
    SelectionStep ignored;
    Stop(ignored);
  } else control_.CancelInspectedRecovery();
  recovery_.progress = RecoveryProgress::Cancelled;
  PublishCurrent();
}
bool DeviceControlRuntime::FinishInspectedRecovery(RecoveryHandle& handle) {
  if (!OwnsRecovery(handle) || recovery_.progress == RecoveryProgress::Pending) return false;
  handle.owner_ = nullptr; handle.serial_ = 0;
  recovery_ = RecoveryIntent();
  return true;
}
bool DeviceControlRuntime::HasRecoveryWork() const {
  return (recovery_.owner && recovery_.progress == RecoveryProgress::Pending) || control_.HasRecoveryWork();
}

bool DeviceControlRuntime::AcquireManualControl(const std::string& leaseId,
                                                uint64_t nowMs) {
  if (externalActivity_ || decorationActive_) return false;
  const bool acquired = control_.AcquireManualControl(leaseId, nowMs);
  PublishCurrent();
  return acquired;
}

bool DeviceControlRuntime::RenewManualLease(const std::string& leaseId,
                                            uint64_t nowMs) {
  const bool hadManualControl = control_.HasManualControl();
  const bool renewed = control_.RenewManualLease(leaseId, nowMs);
  RecordLeaseLoss(hadManualControl);
  PublishCurrent();
  return renewed;
}

bool DeviceControlRuntime::DisconnectManualControl(SelectionStep& step) {
  const bool hadManualControl = control_.HasManualControl();
  const bool stopped = control_.DisconnectManualControl(step);
  RecordLeaseLoss(hadManualControl);
  PublishCurrent();
  return stopped;
}

bool DeviceControlRuntime::DisconnectManualControl(
    const std::string& leaseId, SelectionStep& step) {
  if (!control_.OwnsManualLease(leaseId)) {
    return false;
  }
  DisconnectManualControl(step);
  return true;
}

bool DeviceControlRuntime::CancelConversation(
    const std::string& leaseId, uint64_t expectedGeneration,
    uint64_t nowMs) {
  if (!control_.OwnsLiveManualLease(leaseId, nowMs) ||
      expectedGeneration != generation_) {
    return false;
  }
  ++generation_;
  PublishCurrent();
  return true;
}

bool DeviceControlRuntime::OwnsLiveManualLease(
    const std::string& leaseId, uint64_t nowMs) const {
  return control_.OwnsLiveManualLease(leaseId, nowMs);
}

CommandReceipt DeviceControlRuntime::AcceptSelection(
    const SelectionCommand& command, bool authenticated, uint64_t nowMs) {
  if (externalActivity_ || decorationActive_) return CommandReceipt(CommandError::Busy);
  const bool hadManualControl = control_.HasManualControl();
  const CommandReceipt receipt =
      control_.AcceptSelection(command, authenticated, nowMs);
  RecordLeaseLoss(hadManualControl);
  PublishCurrent();
  return receipt;
}

CommandReceipt DeviceControlRuntime::ConfirmReload(
    const InventoryReloadCommand& command, bool authenticated,
    uint64_t nowMs) {
  if (externalActivity_ || decorationActive_) return CommandReceipt(CommandError::Busy);
  const bool hadManualControl = control_.HasManualControl();
  const CommandReceipt receipt =
      control_.ConfirmReload(command, authenticated, nowMs);
  RecordLeaseLoss(hadManualControl);
  PublishCurrent();
  return receipt;
}

bool DeviceControlRuntime::BeginAcceptedSelection(uint64_t nowMs,
                                                  SelectionStep& step) {
  if (externalActivity_ || decorationActive_) return false;
  const bool hadManualControl = control_.HasManualControl();
  const bool started = control_.BeginAcceptedSelection(nowMs, step);
  RecordLeaseLoss(hadManualControl);
  PublishCurrent();
  return started;
}

bool DeviceControlRuntime::ReserveAutonomousPresentation(
    const std::string& proposalId, uint8_t compartment,
    const ConversationRuntimeSnapshot& expectedSnapshot, uint64_t fingerprint) {
  if ((offlineAuthority_ && !offlineAdmission_) || externalActivity_ || decorationActive_ ||
      presentation_ == nullptr || presentation_->State() != WandPresentationState::Idle ||
      !runtime_.Matches(expectedSnapshot)) return false;
  return control_.ReserveAutonomousPresentation(proposalId, compartment,
      expectedSnapshot.deviceRevision, fingerprint, expectedSnapshot.operationEpoch);
}

bool DeviceControlRuntime::BeginAutonomousPresentation(
    const std::string& proposalId, uint8_t compartment,
    const ConversationRuntimeSnapshot& expectedSnapshot, uint64_t fingerprint) {
  if ((offlineAuthority_ && !offlineAdmission_) || externalActivity_ || decorationActive_ || presentation_ == nullptr ||
      presentation_->State() != WandPresentationState::Idle ||
      !runtime_.Matches(expectedSnapshot)) {
    return false;
  }
  const bool started = control_.BeginAutonomousPresentation(
      proposalId, compartment, expectedSnapshot.deviceRevision, fingerprint,
      expectedSnapshot.operationEpoch);
  PublishCurrent();
  return started;
}

bool DeviceControlRuntime::CompleteAutonomousPresentation(
    uint8_t compartment, bool (*postPersistCheck)(void*), void* context) {
  return AdvanceAutonomousCompletion(compartment, postPersistCheck, context) ==
      PresentationProgress::Succeeded;
}

bool DeviceControlRuntime::ValidateClaimedAutonomousPresentation(
    const PendingProposal::ClaimedProposal& claim, uint64_t fingerprint,
    uint64_t calibrationRevision, uint64_t nowMs) const {
  const auto& proposal = claim.Proposal();
  const auto& snapshot = claim.Snapshot();
  const bool owned = admission_.owner != nullptr;
  if (!claim.IsLive(nowMs) || proposal.proposalId.empty() || proposal.proposalId.size() > 96 ||
      proposal.compartment < 1 || proposal.compartment > 6 ||
      proposal.generation != snapshot.generation || proposal.deviceRevision != snapshot.deviceRevision ||
      proposal.operationEpoch != snapshot.operationEpoch || proposal.catalogVersion != snapshot.catalogVersion ||
      snapshot.catalogVersion != catalogVersion_ || snapshot.generation != generation_ ||
      snapshot.deviceRevision != control_.Revision() || snapshot.operationEpoch != control_.OperationEpoch() ||
      calibrationRevision == 0 || calibrationRevision != control_.CalibrationRevision() ||
      (snapshot.operationEpoch != 0 && fingerprint == 0) ||
      (snapshot.readyMask & (1u << (proposal.compartment - 1))) == 0 ||
      (control_.ReadyMask() & (1u << (proposal.compartment - 1))) == 0 ||
      externalActivity_ || decorationActive_ || control_.HasManualControl() ||
      control_.HasPendingSelection() || control_.HasAutonomousPresentation() ||
      control_.HasAutonomousCompletionFault() || selection_.IsBusy() || presentation_ == nullptr ||
      (presentation_->State() != WandPresentationState::Idle &&
       presentation_->State() != WandPresentationState::AwaitingAdmission)) return false;
  if (owned) {
    if (admission_.owner != &claim || admission_.proposalId != proposal.proposalId ||
        admission_.slot != proposal.compartment || admission_.fingerprint != fingerprint ||
        admission_.calibration != calibrationRevision || admission_.storedAt != claim.StoredAtMs() ||
        admission_.deadline != claim.DeadlineMs() || admission_.generation != snapshot.generation ||
        admission_.revision != snapshot.deviceRevision || admission_.epoch != snapshot.operationEpoch ||
        admission_.snapshotVersion != snapshot.version || admission_.offline != offlineAuthority_) return false;
  } else if ((offlineAuthority_ && !offlineAdmission_) || !runtime_.Matches(snapshot)) {
    return false;
  }
  // Only this exact retained admission may consume its own queued Begin/Reserve
  // after publication is invalidated or its committed snapshot arrives early.
  const bool ownStorage = owned && control_.HasPendingAutonomousAdmission(proposal.proposalId,
      proposal.compartment, snapshot.deviceRevision, fingerprint, snapshot.operationEpoch);
  return ownStorage || (!control_.HasStorageWork() && !control_.NeedsRecovery());
}

bool DeviceControlRuntime::CaptureClaimedAdmission(const PendingProposal::ClaimedProposal& claim,
    uint64_t fingerprint, uint64_t calibrationRevision, uint64_t nowMs) {
  if (!ValidateClaimedAutonomousPresentation(claim, fingerprint, calibrationRevision, nowMs)) return false;
  if (admission_.owner != nullptr) return true;
  const auto& proposal = claim.Proposal();
  const auto& snapshot = claim.Snapshot();
  admission_.owner = &claim;
  admission_.proposalId = proposal.proposalId;
  admission_.slot = proposal.compartment;
  admission_.fingerprint = fingerprint;
  admission_.calibration = calibrationRevision;
  admission_.storedAt = claim.StoredAtMs();
  admission_.deadline = claim.DeadlineMs();
  admission_.generation = snapshot.generation;
  admission_.revision = snapshot.deviceRevision;
  admission_.epoch = snapshot.operationEpoch;
  admission_.snapshotVersion = snapshot.version;
  admission_.offline = offlineAuthority_;
  return true;
}

DeviceControlRuntime::PresentationProgress DeviceControlRuntime::ReserveClaimedAutonomousPresentation(
    const PendingProposal::ClaimedProposal& claim, uint64_t fingerprint,
    uint64_t calibrationRevision, uint64_t nowMs) {
  if (claim.Snapshot().operationEpoch == 0 ||
      !CaptureClaimedAdmission(claim, fingerprint, calibrationRevision, nowMs))
    return PresentationProgress::Rejected;
  const auto& proposal = claim.Proposal();
  const bool waitingForAck = control_.HasPendingAutonomousAdmission(proposal.proposalId,
      proposal.compartment, proposal.deviceRevision, fingerprint, proposal.operationEpoch);
  const bool reserved = (!waitingForAck && control_.HasAutonomousReservation(proposal.proposalId,
      proposal.compartment, fingerprint, proposal.operationEpoch)) || control_.ReserveAutonomousPresentation(proposal.proposalId,
          proposal.compartment, proposal.deviceRevision, fingerprint, proposal.operationEpoch);
  PublishCurrent();
  if (reserved) return PresentationProgress::Succeeded;
  if (control_.LastAutonomousAdmissionProgress() == DeviceControl::AutonomousAdmissionProgress::Pending)
    return PresentationProgress::Pending;
  CancelClaimedAutonomousPresentation();
  return PresentationProgress::Rejected;
}

DeviceControlRuntime::PresentationProgress DeviceControlRuntime::BeginClaimedAutonomousPresentation(
    const PendingProposal::ClaimedProposal& claim, uint64_t fingerprint,
    uint64_t calibrationRevision, uint64_t nowMs) {
  if (!CaptureClaimedAdmission(claim, fingerprint, calibrationRevision, nowMs))
    return PresentationProgress::Rejected;
  const auto& proposal = claim.Proposal();
  const bool started = control_.BeginAutonomousPresentation(proposal.proposalId, proposal.compartment,
      proposal.deviceRevision, fingerprint, proposal.operationEpoch);
  PublishCurrent();
  if (started) {
    admission_ = OwnedAdmission();
    return PresentationProgress::Succeeded;
  }
  if (control_.LastAutonomousAdmissionProgress() == DeviceControl::AutonomousAdmissionProgress::Pending)
    return PresentationProgress::Pending;
  CancelClaimedAutonomousPresentation();
  return PresentationProgress::Rejected;
}

void DeviceControlRuntime::CancelClaimedAutonomousPresentation() {
  admission_ = OwnedAdmission();
  control_.CancelAutonomousReservation();
  PublishCurrent();
}

bool DeviceControlRuntime::HasValidatedAutonomousCompletion() const {
  return completion_.phase == CompletionPhase::Proof &&
      completion_.generation == generation_ && !control_.HasAutonomousCompletionFault();
}

DeviceControlRuntime::PresentationProgress
DeviceControlRuntime::RejectAutonomousCompletion() {
  completion_ = AutonomousCompletion();
  // Completion may be called from WandPresentation itself. Stop the safety
  // owner directly so this path cannot recursively enter its caller.
  SelectionStep step;
  control_.Stop(step);
  ++generation_;
  PublishCurrent();
  return PresentationProgress::Rejected;
}

DeviceControlRuntime::PresentationProgress
DeviceControlRuntime::AdvanceAutonomousCompletion(
    uint8_t compartment, bool (*postPersistCheck)(void*), void* context) {
  if (completion_.phase == CompletionPhase::None) {
    if (!control_.HasAutonomousPresentation() || control_.HasAutonomousCompletionFault())
      return PresentationProgress::Rejected;
    completion_.phase = CompletionPhase::Closing;
    completion_.generation = generation_;
    completion_.compartment = compartment;
    completion_.postPersistCheck = postPersistCheck;
    completion_.context = context;
  } else if (completion_.compartment != compartment ||
             completion_.postPersistCheck != postPersistCheck ||
             completion_.context != context) {
    // Only the original request may consume its close/proof acknowledgement.
    return PresentationProgress::Rejected;
  }
  if (completion_.generation != generation_ || control_.HasAutonomousCompletionFault())
    return RejectAutonomousCompletion();

  if (completion_.phase == CompletionPhase::Closing) {
    if (!control_.CompleteAutonomousPresentation(compartment)) {
      if (control_.IsAutonomousCompletionStoragePending() && !control_.HasAutonomousCompletionFault()) {
        PublishCurrent();
        return PresentationProgress::Pending;
      }
      return RejectAutonomousCompletion();
    }
    // A timing observation authorizes proof only after durable close. Capture
    // it once; later proof acknowledgement latency is bookkeeping, not motion.
    completion_.phase = CompletionPhase::Validating;
    const uint64_t originalGeneration = completion_.generation;
    const bool timely = postPersistCheck == nullptr || postPersistCheck(context);
    if (!timely || generation_ != originalGeneration || control_.HasAutonomousCompletionFault() ||
        completion_.phase != CompletionPhase::Validating)
      return RejectAutonomousCompletion();
    completion_.phase = CompletionPhase::Proof;
  }
  if (completion_.phase != CompletionPhase::Proof)
    return PresentationProgress::Rejected;
  if (!control_.ConfirmTimedPresentation()) {
    if (control_.IsAutonomousCompletionStoragePending() &&
        HasValidatedAutonomousCompletion()) {
      PublishCurrent();
      return PresentationProgress::Pending;
    }
    return RejectAutonomousCompletion();
  }
  if (!HasValidatedAutonomousCompletion()) return RejectAutonomousCompletion();
  completion_ = AutonomousCompletion();
  PublishCurrent();
  return PresentationProgress::Succeeded;
}

void DeviceControlRuntime::FaultAutonomousPresentation() {
  admission_ = OwnedAdmission();
  completion_ = AutonomousCompletion();
  control_.FaultAutonomousPresentation();
  PublishCurrent();
}

bool DeviceControlRuntime::Tick(uint64_t nowMs, SelectionStep& step) {
  const bool hadManualControl = control_.HasManualControl();
  const bool hadPendingSelection = control_.HasPendingSelection();
  // The control tick also expires an idle manual lease and watches loop gaps.
  const bool producedByControl = control_.Tick(nowMs, step);
  if (control_.HasAutonomousPresentation() && selection_.NeedsRecovery()) {
    control_.FaultAutonomousPresentation();
  }
  if (presentation_ != nullptr) {
    const WandPresentationState state = presentation_->State();
    if (selection_.NeedsRecovery() &&
        state != WandPresentationState::Idle &&
        state != WandPresentationState::Fault) {
      presentation_->Stop();
    } else {
      presentation_->Tick(nowMs);
    }
  }
  const bool producedStep = producedByControl ||
      (!hadPendingSelection &&
       selection_.Update(static_cast<uint32_t>(nowMs), step));
  const bool lostManualControl =
      hadManualControl && !control_.HasManualControl();
  if (lostManualControl ||
      (producedStep && step.effect == SelectionEffect::Stopped)) {
    if (recovery_.owner && (recovery_.progress == RecoveryProgress::Pending || recovery_.progress == RecoveryProgress::Succeeded))
      recovery_.progress = RecoveryProgress::Cancelled;
    ++generation_;
  }
  PublishCurrent();
  return producedStep;
}

bool DeviceControlRuntime::Stop(SelectionStep& step) {
  if (recovery_.owner && (recovery_.progress == RecoveryProgress::Pending || recovery_.progress == RecoveryProgress::Succeeded))
    recovery_.progress = RecoveryProgress::Cancelled;
  admission_ = OwnedAdmission();
  offlinePreparation_ = OfflinePreparation();
  completion_ = AutonomousCompletion();
  const bool hadActivePresentation =
      presentation_ != nullptr &&
      presentation_->State() != WandPresentationState::Idle &&
      presentation_->State() != WandPresentationState::Fault;
  if (hadActivePresentation) {
    presentation_->Stop();
  }
  const bool stopped = control_.Stop(step);
  ++generation_;
  PublishCurrent();
  return stopped || hadActivePresentation;
}

bool DeviceControlRuntime::PublishCurrent() {
  if (externalActivity_ || offlinePreparation_.active || (decorationActive_ && !decorationPreservesSnapshot_) || control_.NeedsRecovery() || control_.HasStorageWork() || control_.HasPendingSelection() ||
      control_.HasAutonomousPresentation() || selection_.IsBusy() ||
      (presentation_ != nullptr &&
       presentation_->State() != WandPresentationState::Idle)) {
    if (published_) {
      runtime_.Invalidate();
      published_ = false;
    }
    return false;
  }

  const uint64_t revision = control_.Revision();
  const uint64_t operationEpoch = control_.OperationEpoch();
  const uint8_t readyMask = control_.ReadyMask();
  if (published_ && publishedGeneration_ == generation_ &&
      publishedRevision_ == revision && publishedReadyMask_ == readyMask &&
      publishedOperationEpoch_ == operationEpoch) {
    return true;
  }

  if (!runtime_.Publish(generation_, revision, readyMask, catalogVersion_, operationEpoch)) {
    runtime_.Invalidate();
    published_ = false;
    return false;
  }

  publishedGeneration_ = generation_;
  publishedRevision_ = revision;
  publishedOperationEpoch_ = operationEpoch;
  publishedReadyMask_ = readyMask;
  published_ = true;
  return true;
}

uint64_t DeviceControlRuntime::Generation() const { return generation_; }

void DeviceControlRuntime::SetExternalActivity(bool active) {
  if (externalActivity_ == active) return;
  externalActivity_ = active;
  if (active) ++generation_;
  PublishCurrent();
}

bool DeviceControlRuntime::HasAutonomousPresentation() const {
  return control_.HasAutonomousPresentation();
}

bool DeviceControlRuntime::CanCapturePresentationFingerprint() const {
  return !externalActivity_ && !decorationActive_ && !offlineAuthority_ &&
      !control_.HasAutonomousReservation() &&
      !control_.NeedsRecovery() && !control_.HasManualControl() &&
      !control_.HasStorageWork() && !control_.HasPendingSelection() && !control_.HasAutonomousPresentation() &&
      !selection_.IsBusy() && presentation_ != nullptr &&
      presentation_->State() == WandPresentationState::Idle;
}

bool DeviceControlRuntime::CanCaptureOwnedPresentationFingerprint() const {
  if (CanCapturePresentationFingerprint()) return true;
  // Only StartOfflinePresentation opens this control-owner scope. Capturing a
  // score hash supplies no journal/admission authority, including when the
  // offline producer already reserved its exact original operation.
  return offlineAuthority_ && offlineAdmission_ && !externalActivity_ && !decorationActive_ &&
      !control_.NeedsRecovery() && !control_.HasManualControl() && !control_.HasStorageWork() &&
      !control_.HasPendingSelection() && !control_.HasAutonomousPresentation() &&
      !selection_.IsBusy() && presentation_ != nullptr &&
      presentation_->State() == WandPresentationState::Idle;
}

bool DeviceControlRuntime::SetOfflineAuthority(bool active) {
  if (control_.NeedsRecovery() || externalActivity_ || decorationActive_ ||
      control_.HasManualControl() || control_.HasStorageWork() || control_.HasPendingSelection() ||
      control_.HasAutonomousPresentation() || selection_.IsBusy() ||
      !presentation_ || presentation_->State() != WandPresentationState::Idle) return false;
  if (offlineAuthority_ != active) {
    offlineAuthority_ = active;
    ++generation_; // Old online snapshots/callbacks can never acquire this epoch.
    runtime_.Invalidate();
    published_ = false;
    PublishCurrent();
  }
  return true;
}

bool DeviceControlRuntime::StartOfflinePresentation(PendingProposal& pending, uint64_t nowMs) {
  if (!offlineAuthority_ || offlineAdmission_ || !presentation_) return false;
  offlineAdmission_ = true;
  const bool started = presentation_->Start(pending, runtime_, nowMs);
  offlineAdmission_ = false;
  return started;
}

bool DeviceControlRuntime::StartOfflinePresentationPolled(PendingProposal& pending, uint64_t nowMs) {
  if (!offlineAuthority_ || offlineAdmission_ || !presentation_ || offlinePreparation_.active) return false;
  offlineAdmission_ = true;
  const bool started = presentation_->StartPolled(pending, runtime_, nowMs);
  offlineAdmission_ = false;
  return started;
}

DeviceControlRuntime::PresentationProgress DeviceControlRuntime::ReserveOfflinePreparation(
    const std::string& id, uint8_t slot, const ConversationRuntimeSnapshot& original,
    uint64_t fingerprint, uint64_t calibrationRevision, uint64_t requestedAtMs, uint64_t nowMs) {
  const auto& held = offlinePreparation_;
  const bool same = !held.active || (held.id == id && held.slot == slot && held.generation == original.generation
      && held.revision == original.deviceRevision && held.epoch == original.operationEpoch
      && held.fingerprint == fingerprint && held.calibration == calibrationRevision
      && held.requestedAt == requestedAtMs && held.snapshotVersion == original.version);
  // A competing retry cannot cancel or consume this owner's original result.
  if (!same) return PresentationProgress::Rejected;
  const bool ownStorage = held.active && control_.HasPendingAutonomousAdmission(id, slot,
      original.deviceRevision, fingerprint, original.operationEpoch);
  const bool valid = offlineAuthority_ && !offlineAdmission_ && !externalActivity_ && !decorationActive_
      && admission_.owner == nullptr && presentation_ && presentation_->State() == WandPresentationState::Idle
      && !id.empty() && slot >= 1 && slot <= 6 && original.operationEpoch && fingerprint
      && original.catalogVersion == catalogVersion_ && original.generation == generation_
      && original.deviceRevision == control_.Revision() && original.operationEpoch == control_.OperationEpoch()
      && calibrationRevision && calibrationRevision == control_.CalibrationRevision()
      && (original.readyMask & (1u << (slot - 1))) && (control_.ReadyMask() & (1u << (slot - 1)))
      && nowMs >= requestedAtMs && nowMs - requestedAtMs <= 18500
      && !control_.NeedsRecovery() && !control_.HasManualControl() && !control_.HasPendingSelection()
      && !control_.HasAutonomousPresentation() && !selection_.IsBusy()
      && (ownStorage || !control_.HasStorageWork()) && (held.active || runtime_.Matches(original));
  if (!valid) { if (held.active) CancelOfflinePreparation(); return PresentationProgress::Rejected; }
  if (!held.active) {
    offlinePreparation_.active = true; offlinePreparation_.id = id; offlinePreparation_.slot = slot;
    offlinePreparation_.generation = original.generation; offlinePreparation_.revision = original.deviceRevision;
    offlinePreparation_.epoch = original.operationEpoch; offlinePreparation_.fingerprint = fingerprint;
    offlinePreparation_.calibration = calibrationRevision; offlinePreparation_.requestedAt = requestedAtMs;
    offlinePreparation_.snapshotVersion = original.version;
  }
  const bool reserved = (!ownStorage && control_.HasAutonomousReservation(id, slot, fingerprint,
      original.operationEpoch)) || control_.ReserveAutonomousPresentation(id, slot,
          original.deviceRevision, fingerprint, original.operationEpoch);
  PublishCurrent();
  if (reserved) return PresentationProgress::Succeeded;
  if (control_.LastAutonomousAdmissionProgress() == DeviceControl::AutonomousAdmissionProgress::Pending)
    return PresentationProgress::Pending;
  CancelOfflinePreparation();
  return PresentationProgress::Rejected;
}

void DeviceControlRuntime::FinishOfflinePreparation() {
  offlinePreparation_ = OfflinePreparation();
  PublishCurrent();
}

void DeviceControlRuntime::CancelOfflinePreparation() {
  if (!offlinePreparation_.active) return;
  offlinePreparation_ = OfflinePreparation();
  control_.CancelAutonomousReservation();
  PublishCurrent();
}

bool DeviceControlRuntime::HandoffToOffline(const std::string& leaseId, uint64_t nowMs) {
  if (externalActivity_ || decorationActive_ || !presentation_ ||
      presentation_->State() != WandPresentationState::Idle ||
      !control_.ReleaseIdleManualControl(leaseId, nowMs)) return false;
  return SetOfflineAuthority(true);
}

void DeviceControlRuntime::RecordLeaseLoss(bool hadManualControl) {
  if (hadManualControl && !control_.HasManualControl()) {
    if (recovery_.owner && (recovery_.progress == RecoveryProgress::Pending || recovery_.progress == RecoveryProgress::Succeeded))
      recovery_.progress = RecoveryProgress::Cancelled;
    ++generation_;
  }
}

bool DeviceControlRuntime::ReserveDecoration(uint64_t generation, bool preserveConversationSnapshot) {
  if (generation != generation_ || externalActivity_ || decorationActive_ ||
      control_.NeedsRecovery() || control_.HasManualControl() ||
      control_.HasStorageWork() || control_.HasPendingSelection() || control_.HasAutonomousPresentation() ||
      selection_.IsBusy() || (presentation_ && presentation_->State() != WandPresentationState::Idle)) return false;
  decorationGeneration_ = generation;
  decorationActive_ = true;
  decorationPreservesSnapshot_ = preserveConversationSnapshot;
  PublishCurrent();
  return true;
}

bool DeviceControlRuntime::ReleaseDecoration(uint64_t generation) {
  if (!decorationActive_ || generation != decorationGeneration_) return false;
  decorationActive_ = false;
  decorationPreservesSnapshot_ = false;
  PublishCurrent();
  return true;
}

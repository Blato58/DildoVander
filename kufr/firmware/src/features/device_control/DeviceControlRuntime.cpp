#include "DeviceControlRuntime.h"

#include "../wand_presentation/WandPresentation.h"

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
  if (presentation_ != nullptr &&
      presentation_->State() != WandPresentationState::Idle &&
      presentation_->State() != WandPresentationState::Fault) {
    return false;
  }
  const bool recovered = control_.RecoverAfterInspection();
  if (recovered && presentation_ != nullptr &&
      presentation_->State() == WandPresentationState::Fault &&
      !presentation_->RecoverAfterInspection(true)) {
    PublishCurrent();
    return false;
  }
  PublishCurrent();
  return recovered;
}

bool DeviceControlRuntime::AcquireManualControl(const std::string& leaseId,
                                                uint64_t nowMs) {
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
  const bool hadManualControl = control_.HasManualControl();
  const CommandReceipt receipt =
      control_.ConfirmReload(command, authenticated, nowMs);
  RecordLeaseLoss(hadManualControl);
  PublishCurrent();
  return receipt;
}

bool DeviceControlRuntime::BeginAcceptedSelection(uint64_t nowMs,
                                                  SelectionStep& step) {
  const bool hadManualControl = control_.HasManualControl();
  const bool started = control_.BeginAcceptedSelection(nowMs, step);
  RecordLeaseLoss(hadManualControl);
  PublishCurrent();
  return started;
}

bool DeviceControlRuntime::BeginAutonomousPresentation(
    const std::string& proposalId, uint8_t compartment,
    const ConversationRuntimeSnapshot& expectedSnapshot) {
  if (presentation_ == nullptr ||
      presentation_->State() != WandPresentationState::Idle ||
      !runtime_.Matches(expectedSnapshot)) {
    return false;
  }
  const bool started = control_.BeginAutonomousPresentation(
      proposalId, compartment, expectedSnapshot.deviceRevision);
  PublishCurrent();
  return started;
}

bool DeviceControlRuntime::CompleteAutonomousPresentation(
    uint8_t compartment) {
  const bool completed =
      control_.CompleteAutonomousPresentation(compartment);
  PublishCurrent();
  return completed;
}

void DeviceControlRuntime::FaultAutonomousPresentation() {
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
    ++generation_;
  }
  PublishCurrent();
  return producedStep;
}

bool DeviceControlRuntime::Stop(SelectionStep& step) {
  const bool hadActivePresentation =
      presentation_ != nullptr &&
      presentation_->State() != WandPresentationState::Idle &&
      presentation_->State() != WandPresentationState::Fault;
  if (hadActivePresentation) {
    presentation_->Stop();
  }
  const bool stopped = control_.Stop(step);
  if (stopped || hadActivePresentation) {
    ++generation_;
  }
  PublishCurrent();
  return stopped || hadActivePresentation;
}

bool DeviceControlRuntime::PublishCurrent() {
  if (control_.NeedsRecovery() || control_.HasPendingSelection() ||
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
  const uint8_t readyMask = control_.ReadyMask();
  if (published_ && publishedGeneration_ == generation_ &&
      publishedRevision_ == revision && publishedReadyMask_ == readyMask) {
    return true;
  }

  if (!runtime_.Publish(generation_, revision, readyMask, catalogVersion_)) {
    runtime_.Invalidate();
    published_ = false;
    return false;
  }

  publishedGeneration_ = generation_;
  publishedRevision_ = revision;
  publishedReadyMask_ = readyMask;
  published_ = true;
  return true;
}

uint64_t DeviceControlRuntime::Generation() const { return generation_; }

bool DeviceControlRuntime::HasAutonomousPresentation() const {
  return control_.HasAutonomousPresentation();
}

void DeviceControlRuntime::RecordLeaseLoss(bool hadManualControl) {
  if (hadManualControl && !control_.HasManualControl()) {
    ++generation_;
  }
}

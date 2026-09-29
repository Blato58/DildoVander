#include "DeviceControlRuntime.h"

DeviceControlRuntime::DeviceControlRuntime(
    DeviceControl& control, SelectionSequence& selection,
    ConversationRuntimeState& runtime, const std::string& catalogVersion)
    : control_(control),
      selection_(selection),
      runtime_(runtime),
      catalogVersion_(catalogVersion) {}

bool DeviceControlRuntime::RecoverAfterInspection() {
  const bool recovered = control_.RecoverAfterInspection();
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

bool DeviceControlRuntime::Tick(uint64_t nowMs, SelectionStep& step) {
  const bool hadManualControl = control_.HasManualControl();
  const bool hadPendingSelection = control_.HasPendingSelection();
  // The control tick also expires an idle manual lease and watches loop gaps.
  const bool producedByControl = control_.Tick(nowMs, step);
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
  const bool stopped = control_.Stop(step);
  if (stopped) {
    ++generation_;
  }
  PublishCurrent();
  return stopped;
}

bool DeviceControlRuntime::PublishCurrent() {
  if (control_.NeedsRecovery() || control_.HasPendingSelection() ||
      selection_.IsBusy()) {
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

void DeviceControlRuntime::RecordLeaseLoss(bool hadManualControl) {
  if (hadManualControl && !control_.HasManualControl()) {
    ++generation_;
  }
}

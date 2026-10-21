#include "DeviceRuntime.h"

#include <cstring>

#include "esp_timer.h"
#include "freertos/task.h"

namespace kufr { namespace runtime {

bool DeviceRuntime::SafeIdle(uint64_t nowMs) {
  auto& selectionSequence = ports_.selectionSequence;
  auto& deviceControl = ports_.deviceControl;
  auto& controlRuntime = ports_.controlRuntime;
  auto& conversation = ports_.conversation;
  auto& backendNetwork = ports_.backendNetwork;
  auto& dance = ports_.dance;
  auto& offline = ports_.offline;
  auto& storageStartup = ports_.storageStartup;
  TurnProposal proposal;
  return !deviceControl.HasManualControl() && !deviceControl.HasPendingSelection() &&
        !controlRuntime.HasStorageWork() && !storageStartup.HasWork() && !selectionSequence.IsBusy() &&
        !controlRuntime.HasAutonomousPresentation() && !controlRuntime.HasDecoration() && !conversation.HasActiveTurn() &&
        !backendNetwork.IsVoiceBusy() && !dance.Reserved() && !offline.HasPendingStorage() && !offline.Active() &&
        !backendNetwork.ReadPendingProposal(proposal, nowMs);
}

void DeviceRuntime::ConfigureAdmission(ControlPass& pass) {
  auto& controlRuntime = ports_.controlRuntime;
  auto& firmwareUpdate = ports_.firmwareUpdate;
  auto& sdAssets = ports_.sdAssets;
  auto& owner = ports_.owner;
  auto& storageStartup = ports_.storageStartup;
  auto& sharedStorage = ports_.sharedStorage;
  auto& storageStarted = ports_.storageStarted;
  const auto otaStatus = firmwareUpdate.Status();
  // Boot verification freezes new interactions while permitting STA recovery.
  pass.needsOtaReservation = sdAssets.Reserved() ||
      (firmwareUpdate.Reserved() && otaStatus.state != "verifying-boot");
  pass.maintaining = !storageStarted || !sharedStorage || !sharedStorage->AllowsAdmission() ||
      controlRuntime.HasRecoveryWork() || !pass.ownsControlPass ||
      firmwareUpdate.Reserved() || sdAssets.Reserved();
  pass.recoveryAdmission = pass.ownsControlPass && storageStarted && !storageStartup.RequiresService() &&
      !firmwareUpdate.Reserved() && !sdAssets.Reserved();
  owner.SetMaintenance(pass.maintaining);
  owner.SetRecoveryAdmission(pass.recoveryAdmission);
  owner.SetDanceAdmission(pass.safeIdle && !pass.maintaining);
}

void DeviceRuntime::ProcessSafety(ControlPass& pass) {
  auto& runtimeState = ports_.runtimeState;
  auto& effects = ports_.effects;
  auto& selectionSequence = ports_.selectionSequence;
  auto& controlRuntime = ports_.controlRuntime;
  auto& backendNetwork = ports_.backendNetwork;
  auto& dance = ports_.dance;
  auto& owner = ports_.owner;
  auto& offline = ports_.offline;
  auto& steps = ports_.steps;
  auto& calibrated = ports_.calibrated;
  if (effects.Healthy()) { hardwareFaultHandled = false; hardwareWasInspected = true; }
  if (calibrated && !effects.Healthy() && !hardwareFaultHandled) {
    hardwareFaultHandled = true;
    SelectionStep stopped;
    dance.StopDance();
    // Initial boot inspection is still required; it does not revoke a saved
    // offline opt-in. A fault after inspection does revoke it like Stop.
    if (hardwareWasInspected) offline.Stop();
    controlRuntime.Stop(stopped);
    backendNetwork.StopVoice();
    steps.OnStep(stopped);
  }
  const bool sampleButton = effects.ButtonPressed();
  if (sampleButton != rawButton) { rawButton = sampleButton; buttonChangedAt = pass.nowMs; }
  if (sampleButton != button && pass.nowMs - buttonChangedAt >= 30) {
    button = sampleButton;
    if (button) {
      buttonPressedAt = pass.nowMs;
      gestureConsumed = false;
      const bool ambientWasActive = offline.AmbientActive();
      if (ambientWasActive) offline.PrepareInteraction();
      if (!ambientWasActive && (offline.Active() || controlRuntime.HasStorageWork() ||
          controlRuntime.HasAutonomousPresentation() || controlRuntime.HasDecoration() || selectionSequence.IsBusy() || dance.Reserved())) {
        pendingButtonAction = 0;
        SelectionStep stopped;
        dance.StopDance();
        offline.Stop();
        controlRuntime.Stop(stopped);
        backendNetwork.StopVoice();
        steps.OnStep(stopped);
        gestureConsumed = true;
      }
    } else if (!gestureConsumed && !pass.maintaining) {
      pendingButtonAction = offline.Armed() ? 1 : controlRuntime.OfflineAuthority() ? 0 : 2;
    }
  }
  // Cancellation is immediate; a newly accepted audio owner waits for the
  // bounded previous player to quiesce so no old completion can mute it.
  if (pendingButtonAction && !pass.maintaining && offline.PrepareInteraction()) {
    const uint8_t action = pendingButtonAction;
    pendingButtonAction = 0;
    if (action == 1) offline.StartNext(pass.nowMs);
    else {
      const auto voice = backendNetwork.VoiceStatus();
      if (voice.phase != "idle" && voice.phase != "unavailable") backendNetwork.StopVoice();
      else {
        ConversationRuntimeSnapshot snapshot;
        if (runtimeState.Read(snapshot)) {
          if (voice.mode == "off") backendNetwork.ConfigureVoice("gpt-live", "marin");
          backendNetwork.StartVoice(GenerateEspIdfConversationUuid(), snapshot);
        }
      }
    }
  }
  if (pass.recoveryAdmission && button && !gestureConsumed && pass.nowMs - buttonPressedAt >= 2000) {
    gestureConsumed = true;
    if (!selectionSequence.IsBusy() && !controlRuntime.HasAutonomousPresentation() && !controlRuntime.HasDecoration() && !dance.Reserved() &&
        effects.RecoverAfterInspection()) controlRuntime.BeginInspectedRecovery(physicalRecovery);
  }
  if (physicalRecovery.IsBound()) {
    const auto progress = controlRuntime.PollInspectedRecovery(physicalRecovery);
    if (progress != DeviceControlRuntime::RecoveryProgress::Pending)
      controlRuntime.FinishInspectedRecovery(physicalRecovery);
  }
  owner.Tick();
}

void DeviceRuntime::Maintain(ControlPass& pass) {
  auto& hardware = ports_.hardware;
  auto& effects = ports_.effects;
  auto& station = ports_.station;
  auto& firmwareUpdate = ports_.firmwareUpdate;
  auto& sdAssets = ports_.sdAssets;
  auto& provisioning = ports_.provisioning;
  auto& storageStartup = ports_.storageStartup;
  auto& offlineAudio = ports_.offlineAudio;
  auto& nvsReady = ports_.nvsReady;
  auto& storageStarted = ports_.storageStarted;
  auto& stationStarted = ports_.stationStarted;
  auto& calibrated = ports_.calibrated;
  pass.safeIdle = SafeIdle(ports_.clock.NowMs());
  const bool selfTestReady = kufr::ota::BootSelfTestReady(
      nvsReady && storageStarted && storageStartup.ImageStorageHealthy() && stationStarted &&
          (!kufr::offline::CompiledBank().complete || offlineAudio.Available()),
      hardware.ProfileSelected(), calibrated, effects);
  firmwareUpdate.Tick(pass.nowMs, station.HasIp(), selfTestReady,
      pass.safeIdle && (pass.ownsControlPass || pass.otaHeld) && !sdAssets.Reserved());
  sdAssets.Tick(pass.safeIdle && (pass.ownsControlPass || pass.otaHeld) && !firmwareUpdate.Reserved());
  ConfigureAdmission(pass);
  provisioning.Tick(pass.nowMs, station.HasIp());
}

void DeviceRuntime::AdvanceShows(ControlPass& pass) {
  auto& effects = ports_.effects;
  auto& deviceControl = ports_.deviceControl;
  auto& controlRuntime = ports_.controlRuntime;
  auto& presentationPort = ports_.presentationPort;
  auto& presentation = ports_.presentation;
  auto& conversation = ports_.conversation;
  auto& backendNetwork = ports_.backendNetwork;
  auto& dance = ports_.dance;
  auto& inbox = ports_.inbox;
  auto& owner = ports_.owner;
  auto& offline = ports_.offline;
  auto& deviceShow = ports_.deviceShow;
  auto& clock = ports_.clock;
  inbox.RunIfNoUrgent([&]() {
    const uint64_t effectNowMs = clock.NowMs();
    dance.Tick(effectNowMs);
    controlRuntime.SetExternalActivity(dance.Reserved());
    presentation.CheckDeadline(effectNowMs);
    presentationPort.Tick(effectNowMs);
    offline.SetVoice(backendNetwork.VoiceStatus().voice);
    deviceShow.SetVoice(backendNetwork.VoiceStatus().voice);
    deviceShow.Tick(effectNowMs, !pass.maintaining && !dance.Reserved() &&
        !deviceControl.HasManualControl(),
        !conversation.HasActiveTurn() && !backendNetwork.IsConversationVoiceBusy());
    if (deviceShow.TakeRemoteStop()) {
      dance.StopDance();
      backendNetwork.StopVoice();
    }
    offline.Tick(effectNowMs, !pass.maintaining && !dance.Reserved() &&
        !deviceControl.HasManualControl() && !conversation.HasActiveTurn() &&
        !backendNetwork.IsVoiceBusy() && !deviceShow.Active() &&
        (!backendNetwork.IsOnline() || controlRuntime.OfflineAuthority()));
  });
  owner.PumpReply(backendNetwork);
  // Owner has applied urgent Stop/lease changes before any autonomous start.
  inbox.RunIfNoUrgent([&]() {
    if (!controlRuntime.OfflineAuthority() && !pass.maintaining && !dance.Reserved() && !deviceControl.HasManualControl() && effects.Healthy())
      backendNetwork.BeginPendingPresentation(presentation, clock.NowMs());
  });
  backendNetwork.FinishPresentation(presentation);
}

void DeviceRuntime::PublishTelemetry() {
  auto& observedHardware = ports_.observedHardware;
  auto& effects = ports_.effects;
  auto& selectionSequence = ports_.selectionSequence;
  auto& deviceControl = ports_.deviceControl;
  auto& controlRuntime = ports_.controlRuntime;
  auto& presentationPort = ports_.presentationPort;
  auto& presentation = ports_.presentation;
  auto& backendNetwork = ports_.backendNetwork;
  auto& dance = ports_.dance;
  auto& showTrace = ports_.showTrace;
  auto& clock = ports_.clock;
  auto& twinMailbox = ports_.twinMailbox;
  auto& observedAudio = ports_.observedAudio;
  const auto& presentationTrace = presentationPort.Diagnostics();
  if (std::strcmp(observedPresentationId.data(), presentationTrace.operationId.data()) != 0 ||
      observedPresentationSequence != presentationTrace.lastSequence) {
    observedPresentationId = presentationTrace.operationId;
    observedPresentationSequence = presentationTrace.lastSequence;
    lastTraceWasDance = false;
  }
  if (dance.Reserved()) lastTraceWasDance = true;
  showTrace.Publish(lastTraceWasDance ? dance.Diagnostics() : presentationTrace, clock.NowMs());
  const uint64_t twinNow = clock.NowMs();
  if (twinNow - twinPublishedAt >= 500) {
    twinPublishedAt = twinNow;
    twinSnapshot.trace = lastTraceWasDance ? dance.Diagnostics() : presentationTrace;
    twinSnapshot.sequence = ++twinSequence;
    twinSnapshot.timeMs = twinNow;
    twinSnapshot.revision = deviceControl.Revision();
    twinSnapshot.generation = controlRuntime.Generation();
    twinSnapshot.calibration = effects.CalibrationRevision();
    twinSnapshot.recovery = selectionSequence.NeedsRecovery() || !effects.Healthy() ||
        (!controlRuntime.HasAutonomousPresentation() && deviceControl.NeedsRecovery());
    twinSnapshot.readyMask = deviceControl.ReadyMask();
    twinSnapshot.slot = presentation.Compartment();
    switch (presentation.State()) {
      case WandPresentationState::Idle:
        twinSnapshot.state = dance.Reserved() ? "dance" : selectionSequence.IsBusy() ? "manual-selection" : "idle";
        twinSnapshot.slot = selectionSequence.IsBusy() ? selectionSequence.SelectedCompartment() : 0;
        break;
      case WandPresentationState::Raising: twinSnapshot.state = "raising"; break;
      case WandPresentationState::Showing: twinSnapshot.state = "showing"; break;
      case WandPresentationState::Lowering: twinSnapshot.state = "lowering"; break;
      case WandPresentationState::Completing: twinSnapshot.state = "lowering"; break;
      case WandPresentationState::AwaitingAdmission: twinSnapshot.state = "idle"; break;
      case WandPresentationState::Fault: twinSnapshot.state = "fault"; break;
    }
    twinSnapshot.pulse = observedHardware.pulse_;
    twinSnapshot.led = observedHardware.led_;
    twinSnapshot.ledKnown = observedHardware.ledKnown_;
    twinSnapshot.enabled = observedHardware.enabled_ && !ports_.hardware.UrgentStopPending();
    twinSnapshot.vibrationMask = observedHardware.vibrationMask_;
    twinSnapshot.audio = backendNetwork.IsVoiceBusy() ? "voice-busy-reported" : observedAudio.Status();
    for (uint8_t slot = 1; slot <= 6; ++slot) {
      twinSnapshot.estimate[slot - 1] = effects.IsResting(slot) ? 1 : effects.IsRaised(slot) ? 2 :
          effects.Healthy() && observedHardware.enabled_ ? 3 : 0;
    }
    twinMailbox.Publish(twinSnapshot);
  }
}

void DeviceRuntime::EndPass(ControlPass& pass) {
  auto& maintenance = ports_.maintenance;
  // Publish idle only after all admissions in this iteration have completed.
  // An old pending journal record still requires inspection; it is not an
  // active actuator and must not prevent offline-safe network/OTA recovery.
  const bool completedSafeIdle = SafeIdle(ports_.clock.NowMs());
  if (pass.ownsControlPass) {
    if (pass.needsOtaReservation) {
      // No idle publication window may admit STA/pairing between enqueueing
      // an exclusive OTA/SD operation and transferring its reservation.
      maintenance.ownership.TransferControl(pass.controlPass, ControlMaintenanceGate::Owner::Ota, maintenance.ota);
    } else {
      maintenance.ownership.EndControl(pass.controlPass, completedSafeIdle);
    }
  } else if (pass.otaHeld && !pass.needsOtaReservation) {
    maintenance.ownership.Release(ControlMaintenanceGate::Owner::Ota, maintenance.ota);
  }
}

ActuatorOwner DeviceRuntime::CurrentOwner(const ControlPass& pass) const {
  if (ports_.hardware.UrgentStopPending()) return ActuatorOwner::StopPending;
  if (pass.maintaining) return ActuatorOwner::Maintenance;
  if (!ports_.effects.Healthy() || ports_.deviceControl.NeedsRecovery()) return ActuatorOwner::Recovery;
  if (ports_.deviceControl.HasManualControl()) return ActuatorOwner::Manual;
  if (ports_.dance.Reserved()) return ActuatorOwner::Dance;
  if (ports_.deviceShow.Active()) return ActuatorOwner::DeviceShow;
  if (ports_.offline.Active()) return ActuatorOwner::OfflineShow;
  if (ports_.controlRuntime.HasAutonomousPresentation() || ports_.controlRuntime.HasDecoration()) return ActuatorOwner::Presentation;
  return ActuatorOwner::Idle;
}

void DeviceRuntime::Step() {
  ControlPass pass;
  pass.ownsControlPass = ports_.maintenance.ownership.BeginControl(pass.controlPass);
  pass.otaHeld = ports_.maintenance.ownership.HeldBy() == ControlMaintenanceGate::Owner::Ota;
  pass.nowMs = ports_.clock.NowMs();
  ports_.hardware.BeginControlPass(static_cast<uint64_t>(esp_timer_get_time()));
  // Input/fault/deadline and local Stop/lease processing precede ordinary work.
  ports_.presentation.CheckDeadline(pass.nowMs);
  ports_.effects.Tick(pass.nowMs);
  pass.safeIdle = SafeIdle(pass.nowMs);
  ConfigureAdmission(pass);
  ProcessSafety(pass);
  // Recompute idle after manual admission. OTA cannot use a stale idle view.
  Maintain(pass);
  AdvanceShows(pass);
  PublishTelemetry();
  snapshot_.owner = CurrentOwner(pass);
  snapshot_.maintenanceOwner = static_cast<uint32_t>(ports_.maintenance.ownership.HeldBy());
  const auto io = ports_.hardware.Timing();
  snapshot_.maximumIoTransferUs = io.maximumTransferUs;
  snapshot_.maximumPassBusUs = io.maximumPassBusUs;
  snapshot_.ioLockRefusals = io.lockRefusals;
  snapshot_.ioBudgetRefusals = io.budgetRefusals;
  snapshot_.coalescedLedFrames = io.coalescedLedFrames;
  snapshot_.overBudgetPasses = io.overBudgetPasses;
  EndPass(pass);
}

void DeviceRuntime::Run() {
  vTaskPrioritySet(nullptr, 6);
  ControlSchedule schedule(static_cast<uint64_t>(esp_timer_get_time()));
  for (;;) {
    const uint64_t nowUs = static_cast<uint64_t>(esp_timer_get_time());
    if (!schedule.Begin(nowUs)) {
      const uint64_t waitUs = schedule.NextReleaseUs() - nowUs;
      const auto ticks = pdMS_TO_TICKS(static_cast<uint32_t>((waitUs + 999) / 1000));
      vTaskDelay(ticks ? ticks : 1);
      continue;
    }
    Step();
    schedule.Finish(static_cast<uint64_t>(esp_timer_get_time()));
    snapshot_.timing = schedule.Timing();
    diagnostics_.Publish(snapshot_);
  }
}

} }

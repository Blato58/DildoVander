#include "LocalControlOwner.h"
#include "../offline_show/OfflineShow.h"

#include <new>
#include "../voice_turn/LocalVoicePort.h"

#include "LocalControlTransport.h"
#include "LocalConversationNetworkPort.h"
#include "LocalConversationReplyPort.h"
#include "../device_control/DeviceControlRuntime.h"

namespace {

bool SameManualEnvelope(const LocalControlMessage& left,
                        const LocalControlMessage& right) {
  // messageId is transport correlation; retries still complete the original ACK.
  return left.kind == right.kind && left.commandId == right.commandId &&
         left.hasOperationEpoch == right.hasOperationEpoch &&
         left.operationEpoch == right.operationEpoch &&
         left.deviceId == right.deviceId && left.bootId == right.bootId &&
         left.controlLeaseId == right.controlLeaseId &&
         left.sequence == right.sequence &&
         left.expectedRevision == right.expectedRevision &&
         left.issuedAtDeviceMs == right.issuedAtDeviceMs &&
         left.maxAgeMs == right.maxAgeMs &&
         left.compartment == right.compartment && left.source == right.source;
}

void WipeText(std::string& text) {
  volatile char* bytes = text.empty() ? nullptr : &text[0];
  for (size_t index = 0; index < text.size(); ++index) {
    bytes[index] = 0;
  }
  text.clear();
}

bool OfflineMutation(LocalControlMessageKind kind) {
  return kind == LocalControlMessageKind::OfflineArm || kind == LocalControlMessageKind::OfflineStart
      || kind == LocalControlMessageKind::Quiet || kind == LocalControlMessageKind::OfflineAmbient
      || kind == LocalControlMessageKind::OfflineSyncAck;
}

bool SameOfflineEnvelope(const LocalControlMessage& left, const LocalControlMessage& right) {
  return left.kind == right.kind && left.messageId == right.messageId && left.deviceId == right.deviceId
      && left.bootId == right.bootId && left.controlLeaseId == right.controlLeaseId
      && left.generation == right.generation && left.expectedRevision == right.expectedRevision
      && left.hasOperationEpoch == right.hasOperationEpoch && left.operationEpoch == right.operationEpoch
      && left.compartment == right.compartment && left.shortVariant == right.shortVariant
      && left.commandId == right.commandId && left.enabled == right.enabled;
}

bool SameRecoveryEnvelope(const LocalControlMessage& left, const LocalControlMessage& right) {
  return left.kind == right.kind && left.messageId == right.messageId && left.deviceId == right.deviceId &&
      left.bootId == right.bootId && left.controlLeaseId == right.controlLeaseId;
}

bool RecoveryMutation(LocalControlMessageKind kind) {
  return OfflineMutation(kind) || kind == LocalControlMessageKind::Acquire || kind == LocalControlMessageKind::Reset ||
      kind == LocalControlMessageKind::Selection || kind == LocalControlMessageKind::Reload ||
      kind == LocalControlMessageKind::VoiceStart || kind == LocalControlMessageKind::VoiceConfigure ||
      kind == LocalControlMessageKind::DanceStart;
}

}  // namespace

LocalControlOwner::LocalControlOwner(
    const std::string& deviceId, const std::string& bootId,
    DeviceControlRuntime& runtime, ConversationRuntimeState& runtimeState,
    ConversationSession& conversation, LocalConversationNetworkPort& network,
    LocalControlInbox& inbox, LocalControlTransport& transport,
    LocalControlOutputPort& output, LocalControlIdPort& ids,
    LocalControlRecoveryGate& recovery, LocalControlStepPort& steps,
    LocalControlClockPort& clock, LocalVoicePort* voice, kufr::dance::LocalDancePort* dance)
    : voice_(voice), dance_(dance), deviceId_(deviceId), bootId_(bootId), runtime_(runtime),
      runtimeState_(runtimeState), conversation_(conversation), network_(network),
      inbox_(inbox), transport_(transport), output_(output), ids_(ids),
      recovery_(recovery), steps_(steps), clock_(clock) {}

LocalControlOwner::~LocalControlOwner() { Close(); }

void LocalControlOwner::Poll() {
  // At most four ordinary entries and three reserved lifecycle/Stop entries.
  // A producer cannot keep the control owner in an unbounded drain loop.
  LocalControlEvent event;
  for (size_t index = 0; index < LocalControlInbox::QueueCapacity + 3; ++index) {
    if (!inbox_.Take(event)) {
      break;
    }
    const uint64_t nowMs = clock_.NowMs();
    if (event.kind == LocalControlEventKind::Connected) {
      Connected(event);
    } else if (SameHandle(event.handle)) {
      switch (event.kind) {
        case LocalControlEventKind::Stop: {
          ClearPendingManual();
          ClearPendingOffline();
          SelectionStep step;
          if (dance_) dance_->StopDance();
          if (voice_) voice_->StopVoice();
          if (offline_) offline_->Stop();
          runtime_.Stop(step);
          ClearPendingRecovery();
          steps_.OnStep(step);
          if (gateway_) {
            gateway_->OnControlTransition();
          }
          Snapshot(nowMs);
          break;
        }
        case LocalControlEventKind::Disconnected:
          Disconnect();
          break;
        case LocalControlEventKind::Message:
          if (inbox_.CanProcess(handle_)) {
            Handle(event.message, nowMs);
          }
          break;
        case LocalControlEventKind::Connected:
          break;
      }
    }
  }
  WipeText(event.message.text);
  if (!session_) {
    return;
  }
  const uint64_t nowMs = clock_.NowMs();
  if (gateway_) {
    gateway_->OnControlTransition();
    gateway_->PollNetworkFailure();
  }
  if (nowMs < connectedAtMs_ ||
      (!hello_ && nowMs - connectedAtMs_ > 10000) ||
      (!session_->LeaseId().empty() &&
       !session_->OwnsLiveLease(bootId_, session_->LeaseId(), nowMs))) {
    Close();
    return;
  }
  if (hello_ && (nowMs < lastSnapshotAtMs_ ||
                nowMs - lastSnapshotAtMs_ >= 1000)) {
    Snapshot(nowMs);
  }
}

void LocalControlOwner::Tick() {
  Poll();
  CommandReceipt receipt;
  LocalControlMessage completed;
  bool manualCompleted = false;
  inbox_.RunIfNoUrgent([&]() {
    if (!manualPending_) return;
    if (!session_ || pendingManualSession_ != session_.get() ||
        !SameHandle(pendingManualHandle_)) {
      ClearPendingManual();
      return;
    }
    receipt = AcceptManualCommand(pendingManual_);
    if (!receipt.deferred) {
      completed = pendingManual_;
      ClearPendingManual();
      manualCompleted = true;
    }
  });
  if (manualCompleted) {
    FinishManualCommand(completed, receipt, clock_.NowMs());
  }
  LocalControlMessage offlineCompleted;
  bool offlineReady = false, offlineResult = false;
  inbox_.RunIfNoUrgent([&]() {
    if (!offlinePending_) return;
    if (!session_ || pendingOfflineSession_ != session_.get() || !SameHandle(pendingOfflineHandle_)) {
      ClearPendingOffline();
      return;
    }
    offlineResult = AcceptOfflineCommand(pendingOffline_, true);
    if (offlineCallPending_) return;
    offlineCompleted = pendingOffline_;
    ClearPendingOffline();
    offlineReady = true;
  });
  if (offlineReady) FinishOfflineCommand(offlineCompleted, offlineResult, clock_.NowMs());
  LocalControlMessage resetCompleted;
  auto recoveryProgress = DeviceControlRuntime::RecoveryProgress::Pending;
  bool resetReady = false;
  inbox_.RunIfNoUrgent([&]() {
    if (!recoveryPending_) return;
    if (!session_ || pendingRecoverySession_ != session_.get() || !SameHandle(pendingRecoveryHandle_) ||
        !session_->OwnsLiveLease(bootId_, pendingRecovery_.controlLeaseId, clock_.NowMs())) {
      ClearPendingRecovery();
      return;
    }
    recoveryProgress = runtime_.PollInspectedRecovery(recoveryHandle_);
    if (recoveryProgress == DeviceControlRuntime::RecoveryProgress::Pending) return;
    resetCompleted = pendingRecovery_;
    runtime_.FinishInspectedRecovery(recoveryHandle_);
    ClearPendingRecovery();
    resetReady = true;
  });
  if (resetReady) FinishReset(resetCompleted, recoveryProgress, clock_.NowMs());
  bool produced = false;
  inbox_.RunIfNoUrgent([this, &produced]() {
    SelectionStep step;
    produced = runtime_.Tick(clock_.NowMs(), step);
    if (produced) {
      steps_.OnStep(step);
    }
  });
  if (produced && gateway_) {
    gateway_->OnControlTransition();
    Snapshot(clock_.NowMs());
  }
}

void LocalControlOwner::Connected(const LocalControlEvent& event) {
  Disconnect();
  handle_ = event.handle;
  connectedAtMs_ = event.connectedAtMs;
  session_.reset(new (std::nothrow) LocalControlSession(
      runtime_, deviceId_, bootId_, connectedAtMs_));
  if (session_) {
    gateway_.reset(new (std::nothrow) LocalConversationGateway(
        *session_, runtime_, runtimeState_, conversation_, network_));
  }
  if (!gateway_) {
    Close();
  }
}

void LocalControlOwner::Disconnect() {
  ClearPendingManual();
  // The offline producer owns already accepted storage work independently.
  // Disconnect abandons only this socket's eventual acknowledgement.
  ClearPendingOffline();
  if (session_) {
    SelectionStep step;
    session_->Disconnect(step);
    steps_.OnStep(step);
    if (gateway_) {
      gateway_->OnControlTransition();
    }
  }
  ClearPendingRecovery();
  gateway_.reset();
  session_.reset();
  handle_ = {};
  hello_ = false;
  includeOperationEpoch_ = false;
}

void LocalControlOwner::Close() {
  const auto handle = handle_;
  Disconnect();
  transport_.Close(handle);
}

bool LocalControlOwner::SameHandle(
    const LocalSocketAdmission::Handle& handle) const {
  return handle_.socket >= 0 && handle_.socket == handle.socket &&
         handle_.generation == handle.generation;
}

void LocalControlOwner::Snapshot(uint64_t nowMs) {
  if (session_ && hello_) {
    if (!output_.ScopedSnapshot(handle_, session_->LeaseId(), nowMs, includeOperationEpoch_)) {
      Close();
    } else {
      lastSnapshotAtMs_ = nowMs;
    }
  }
}

void LocalControlOwner::Handle(
    const LocalControlMessage& message, uint64_t nowMs) {
  if (!session_ || message.deviceId != deviceId_) {
    Close();
    return;
  }
  if (!hello_) {
    if (message.kind != LocalControlMessageKind::Hello ||
        !session_->Hello(1, message.deviceId, nowMs)) {
      Close();
      return;
    }
    hello_ = true;
    gateway_->OnControlTransition();
    if (!output_.Welcome(handle_, message.messageId, nowMs)) {
      Close();
    } else {
      lastSnapshotAtMs_ = nowMs;
    }
    return;
  }
  if (message.kind == LocalControlMessageKind::Hello ||
      message.bootId != bootId_) {
    Close();
    return;
  }
  // Refusals cannot revive a lease that expired while waiting in the inbox.
  if (!session_->LeaseId().empty() &&
      !session_->OwnsLiveLease(bootId_, session_->LeaseId(), nowMs)) {
    Close();
    return;
  }
  if (message.kind == LocalControlMessageKind::Reset &&
      !session_->OwnsLiveLease(bootId_, message.controlLeaseId, nowMs)) {
    Close();
    return;
  }
  if (manualPending_ &&
      (message.kind == LocalControlMessageKind::Selection ||
       message.kind == LocalControlMessageKind::Reload)) {
    if (SameManualEnvelope(message, pendingManual_)) return;
    const CommandError error = message.commandId == pendingManual_.commandId
        ? CommandError::IdConflict : CommandError::Busy;
    if (!output_.CommandAck(handle_, message,
                            {error, CommandOutcome::Rejected, false})) {
      Close();
    }
    return;
  }
  bool accepted = true;
  // Stop/cancel/status remain available while network/OTA owns the idle
  // reservation. A queued controller command cannot start new work then.
  const bool dancing = dance_ && kufr::dance::IsDanceActive(dance_->DanceState());
  if (recoveryPending_ && RecoveryMutation(message.kind)) {
    if (SameRecoveryEnvelope(message, pendingRecovery_)) return;
    if (!RefuseBusy(message)) Close();
    else Snapshot(nowMs);
    return;
  }
  if (offlinePending_ && OfflineMutation(message.kind)) {
    if (SameOfflineEnvelope(message, pendingOffline_)) return;
    if (!output_.OfflineAck(handle_, message, false, "busy_or_stale", offline_->StatusJson(), "")) Close();
    return;
  }
  if (offlinePending_ && (message.kind == LocalControlMessageKind::Acquire ||
      message.kind == LocalControlMessageKind::VoiceStart || message.kind == LocalControlMessageKind::Selection ||
      message.kind == LocalControlMessageKind::Reload || message.kind == LocalControlMessageKind::Reset)) {
    if (!RefuseBusy(message)) Close();
    else Snapshot(nowMs);
    return;
  }
  if ((maintenance_ || dancing) && (message.kind == LocalControlMessageKind::Acquire ||
      message.kind == LocalControlMessageKind::VoiceStart ||
      message.kind == LocalControlMessageKind::Selection ||
      message.kind == LocalControlMessageKind::Reload ||
      (message.kind == LocalControlMessageKind::Reset && !(recoveryAdmission_ && !dancing)))) {
    if (!RefuseBusy(message)) {
      Close();
    } else {
      Snapshot(nowMs);
    }
    return;
  }
  switch (message.kind) {
    case LocalControlMessageKind::OfflinePreflight:
    case LocalControlMessageKind::OfflineArm:
    case LocalControlMessageKind::OfflineStart:
    case LocalControlMessageKind::Quiet:
    case LocalControlMessageKind::OfflineOutbox:
    case LocalControlMessageKind::OfflineAmbient:
    case LocalControlMessageKind::OfflineSyncAck:
      OfflineCommand(message, nowMs);
      return;
    case LocalControlMessageKind::StateGet:
      includeOperationEpoch_ = includeOperationEpoch_ || message.includeOperationEpoch;
      Snapshot(nowMs);
      return;
    case LocalControlMessageKind::Acquire: {
      LocalControlError error = LocalControlError::None;
      if (!session_->LeaseId().empty()) {
        error = LocalControlError::ControllerBusy;
      } else if (runtime_.NeedsRecovery()) {
        error = LocalControlError::RecoveryRequired;
      } else {
        const std::string leaseId = ids_.NewLeaseId();
        if (leaseId.empty()) {
          Close();
          return;
        }
        if (voice_) voice_->StopVoice();
        if ((!offline_ || (offline_->PrepareInteraction() &&
             (!runtime_.OfflineAuthority() || offline_->Leave()))) &&
            session_->Acquire(bootId_, leaseId, nowMs)) {
          error = LocalControlError::None;
        } else {
          error = runtime_.NeedsRecovery() ? LocalControlError::RecoveryRequired
                                           : LocalControlError::Busy;
        }
      }
      accepted = ControlAck(message, error);
      break;
    }
    case LocalControlMessageKind::Release: {
      SelectionStep step;
      accepted = session_->Release(bootId_, message.controlLeaseId, step);
      if (accepted) {
        steps_.OnStep(step);
        accepted = ControlAck(message, LocalControlError::None);
      }
      break;
    }
    case LocalControlMessageKind::Heartbeat:
      accepted = session_->Heartbeat(bootId_, message.controlLeaseId, nowMs);
      if (accepted) {
        accepted = output_.Heartbeat(handle_, message, nowMs);
      }
      break;
    case LocalControlMessageKind::VoiceConfigure:
      accepted = output_.VoiceAck(handle_, message, !dancing && voice_ &&
          (!offline_ || offline_->PrepareInteraction()) && voice_->ConfigureVoice(message.voiceMode, message.voice));
      break;
    case LocalControlMessageKind::VoiceStart: {
      ConversationRuntimeSnapshot snapshot;
      const bool online = (!offline_ || offline_->PrepareInteraction()) &&
          (!runtime_.OfflineAuthority() || (offline_ && offline_->Leave()));
      const bool ready = online && voice_ && session_->LeaseId().empty() && runtimeState_.Read(snapshot) &&
                         snapshot.deviceRevision == message.expectedRevision;
      const bool started = ready && voice_->StartVoiceWithImageConsent(message.messageId, snapshot, message.imageConsent);
      accepted = output_.VoiceAck(handle_, message, started);
      break;
    }
    case LocalControlMessageKind::VoiceStop:
      if (voice_ && !dancing) voice_->StopVoice();
      accepted = output_.VoiceAck(handle_, message, voice_ != nullptr && !dancing);
      break;
    case LocalControlMessageKind::DanceStart: {
      ConversationRuntimeSnapshot snapshot;
      const auto voice = voice_ ? voice_->VoiceStatus() : LocalVoiceStatus{};
      const bool voiceIdle = voice.phase == "idle" || voice.phase == "unavailable" ||
                             voice.phase == "completed" || voice.phase == "failed";
      bool started = false;
      inbox_.RunIfCurrent(handle_, [&]() {
        started = (!offline_ || offline_->PrepareInteraction()) &&
            !runtime_.OfflineAuthority() && dance_ && danceAdmission_ && !maintenance_ && !dancing && voiceIdle &&
            session_->LeaseId().empty() && !conversation_.HasActiveTurn() &&
            runtimeState_.Read(snapshot) && snapshot.deviceRevision == message.expectedRevision &&
            dance_->StartDance(message.messageId);
        if (started) runtime_.SetExternalActivity(true);
      });
      accepted = output_.DanceAck(handle_, message, started);
      break;
    }
    case LocalControlMessageKind::DanceStop:
      if (dance_) dance_->StopDance();
      accepted = output_.DanceAck(handle_, message, dance_ != nullptr);
      break;
    case LocalControlMessageKind::Text: {
      // The former turns/opening backend is retired. Older controllers get a
      // typed upgrade requirement before any cloud request or local turn starts.
      accepted = output_.ConversationAck(handle_, message,
          {ConversationCommandError::EngineUpgradeRequired, false});
      break;
    }
    case LocalControlMessageKind::Cancel: {
      const ConversationCommandError error = gateway_->Cancel(
          message.bootId, message.conversationId, message.controlLeaseId,
          message.generation, nowMs);
      accepted = output_.ConversationAck(handle_, message, {error, false});
      break;
    }
    case LocalControlMessageKind::Reset: {
      Reset(message, nowMs);
      return;
    }
    case LocalControlMessageKind::Selection:
      Selection(message, nowMs);
      return;
    case LocalControlMessageKind::Reload:
      Reload(message, nowMs);
      return;
    case LocalControlMessageKind::Stop:
    case LocalControlMessageKind::Hello:
      accepted = false;
      break;
  }
  if (!accepted) {
    Close();
  } else if (session_) {
    gateway_->OnControlTransition();
    if (message.kind != LocalControlMessageKind::Heartbeat) {
      Snapshot(nowMs);
    }
  }
}

void LocalControlOwner::ClearPendingOffline() {
  WipeText(pendingOffline_.text);
  pendingOffline_ = {};
  pendingOfflineHandle_ = {};
  pendingOfflineSession_ = nullptr;
  offlinePending_ = offlineArmHandoff_ = offlineCallPending_ = false;
  offlineArmGeneration_ = offlineArmRevision_ = 0;
}

void LocalControlOwner::ClearPendingRecovery() {
  if (recoveryHandle_.IsBound()) {
    runtime_.CancelInspectedRecovery(recoveryHandle_);
    runtime_.FinishInspectedRecovery(recoveryHandle_);
  }
  pendingRecovery_ = {};
  pendingRecoveryHandle_ = {};
  pendingRecoverySession_ = nullptr;
  recoveryPending_ = false;
}

void LocalControlOwner::Reset(const LocalControlMessage& message, uint64_t nowMs) {
  if (manualPending_ || offlinePending_) {
    if (!RefuseBusy(message)) Close();
    return;
  }
  auto progress = DeviceControlRuntime::RecoveryProgress::Rejected;
  const bool current = inbox_.RunIfCurrent(handle_, [&]() {
    if (!session_->OwnsLiveLease(bootId_, message.controlLeaseId, clock_.NowMs()) ||
        !recovery_.InspectionAllowsRecovery()) return;
    progress = runtime_.BeginInspectedRecovery(recoveryHandle_);
    if (progress != DeviceControlRuntime::RecoveryProgress::Pending) return;
    pendingRecovery_ = message;
    pendingRecoveryHandle_ = handle_;
    pendingRecoverySession_ = session_.get();
    recoveryPending_ = true;
  });
  if (!current || progress == DeviceControlRuntime::RecoveryProgress::Pending) return;
  runtime_.FinishInspectedRecovery(recoveryHandle_);
  FinishReset(message, progress, nowMs);
}

void LocalControlOwner::FinishReset(const LocalControlMessage& message,
    DeviceControlRuntime::RecoveryProgress progress, uint64_t nowMs) {
  if (!ControlAck(message, progress == DeviceControlRuntime::RecoveryProgress::Succeeded
      ? LocalControlError::None : LocalControlError::RecoveryRequired)) Close();
  else if (session_) {
    gateway_->OnControlTransition();
    Snapshot(nowMs);
  }
}

void LocalControlOwner::OfflineCommand(const LocalControlMessage& message, uint64_t nowMs) {
  bool result = false;
  const bool current = inbox_.RunIfCurrent(handle_, [&]() {
    if (!offlinePending_) offlineArmHandoff_ = false;
    result = AcceptOfflineCommand(message, false);
    if (!offlineCallPending_) return;
    pendingOffline_ = message;
    pendingOfflineHandle_ = handle_;
    pendingOfflineSession_ = session_.get();
    offlinePending_ = true;
  });
  if (!current || offlineCallPending_) return;
  FinishOfflineCommand(message, result, nowMs);
}

bool LocalControlOwner::AcceptOfflineCommand(const LocalControlMessage& message, bool retry) {
  offlineCallPending_ = false;
  if (!offline_) return false;
  if (voice_) offline_->SetVoice(voice_->VoiceStatus().voice);
  const bool dancing = dance_ && kufr::dance::IsDanceActive(dance_->DanceState());
  bool result = true;
  if (message.kind == LocalControlMessageKind::OfflineArm) {
    if (maintenance_ || dancing) return false;
    if (!retry || !offlineArmHandoff_) {
      ConversationRuntimeSnapshot before;
      const auto voice = voice_ ? voice_->VoiceStatus() : LocalVoiceStatus{};
      const bool voiceIdle = voice.phase == "idle" || voice.phase == "unavailable" ||
          voice.phase == "completed" || voice.phase == "failed";
      if (!voiceIdle || !session_->OwnsLiveLease(bootId_, message.controlLeaseId, clock_.NowMs()) ||
          !runtimeState_.Read(before) || before.generation != message.generation ||
          before.deviceRevision != message.expectedRevision || std::string(offline_->Preflight()) != "ready") return false;
      if (voice_) voice_->StopVoice();
      if (!session_->HandoffToOffline(bootId_, message.controlLeaseId, clock_.NowMs())) return false;
      ConversationRuntimeSnapshot after;
      if (!runtimeState_.Read(after)) return false;
      offlineArmGeneration_ = after.generation;
      offlineArmRevision_ = after.deviceRevision;
      offlineArmHandoff_ = true;
    }
    result = offline_->Arm(offlineArmGeneration_, offlineArmRevision_);
  } else if (message.kind == LocalControlMessageKind::OfflineStart) {
    if (maintenance_ || dancing) return false;
    result = offline_->Start(message.messageId, message.compartment, message.shortVariant,
        message.generation, message.expectedRevision, clock_.NowMs(), false, message.operationEpoch);
  } else if (message.kind == LocalControlMessageKind::Quiet) result = offline_->Quiet(message.enabled);
  else if (message.kind == LocalControlMessageKind::OfflineAmbient) result = offline_->ConfigureAmbient(message.enabled);
  else if (message.kind == LocalControlMessageKind::OfflineSyncAck)
    result = offline_->Acknowledge(message.commandId, message.operationEpoch);
  offlineCallPending_ = !result && offline_->LastOperationIsPending();
  return result;
}

void LocalControlOwner::FinishOfflineCommand(const LocalControlMessage& message, bool result, uint64_t nowMs) {
  std::string error;
  if (!result) {
    error = offline_ ? offline_->Preflight() : "unsupported";
    if (error == "ready") error = "busy_or_stale";
  }
  if (!output_.OfflineAck(handle_, message, result, error, offline_ ? offline_->StatusJson() : "null",
      message.kind == LocalControlMessageKind::OfflineOutbox && offline_ ? offline_->OutboxJson(includeOperationEpoch_) : "")) {
    Close();
  } else if (session_) {
    gateway_->OnControlTransition();
    Snapshot(nowMs);
  }
}

bool LocalControlOwner::ControlAck(
    const LocalControlMessage& message, LocalControlError error) {
  // Older strict v1 clients only understand the authoritative snapshot.
  return !message.acknowledge || output_.ControlAck(handle_, message, error);
}

bool LocalControlOwner::RefuseBusy(const LocalControlMessage& message) {
  switch (message.kind) {
    case LocalControlMessageKind::Acquire:
    case LocalControlMessageKind::Reset:
      return ControlAck(message, LocalControlError::Busy);
    case LocalControlMessageKind::VoiceStart:
    case LocalControlMessageKind::VoiceConfigure:
      return output_.VoiceAck(handle_, message, false);
    case LocalControlMessageKind::DanceStart:
      return output_.DanceAck(handle_, message, false);
    case LocalControlMessageKind::OfflineArm:
    case LocalControlMessageKind::OfflineStart:
    case LocalControlMessageKind::Quiet:
    case LocalControlMessageKind::OfflineAmbient:
    case LocalControlMessageKind::OfflineSyncAck:
      return output_.OfflineAck(handle_, message, false, "busy_or_stale",
          offline_ ? offline_->StatusJson() : "null", "");
    case LocalControlMessageKind::Selection:
    case LocalControlMessageKind::Reload:
      return output_.CommandAck(handle_, message,
          {CommandError::Busy, CommandOutcome::Rejected, false});
    default:
      return false;
  }
}

void LocalControlOwner::Selection(
    const LocalControlMessage& message, uint64_t nowMs) {
  ManualCommand(message, nowMs);
}

void LocalControlOwner::Reload(
    const LocalControlMessage& message, uint64_t nowMs) {
  ManualCommand(message, nowMs);
}

void LocalControlOwner::ClearPendingManual() {
  WipeText(pendingManual_.text);
  pendingManual_ = {};
  pendingManualHandle_ = {};
  pendingManualSession_ = nullptr;
  manualPending_ = false;
}

void LocalControlOwner::ManualCommand(
    const LocalControlMessage& message, uint64_t nowMs) {
  CommandReceipt receipt;
  const bool current = inbox_.RunIfCurrent(handle_, [&]() {
    receipt = AcceptManualCommand(message);
    if (receipt.deferred) {
      pendingManual_ = message;
      pendingManualHandle_ = handle_;
      pendingManualSession_ = session_.get();
      manualPending_ = true;
    }
  });
  if (!current || receipt.deferred) return;
  FinishManualCommand(message, receipt, nowMs);
}

CommandReceipt LocalControlOwner::AcceptManualCommand(
    const LocalControlMessage& message) {
  // Called only under the inbox guard. Recheck time/lease on every storage retry.
  const uint64_t acceptedAtMs = clock_.NowMs();
  const bool ownsLease = session_->OwnsLiveLease(
      bootId_, message.controlLeaseId, acceptedAtMs);
  if (maintenance_ || (dance_ && kufr::dance::IsDanceActive(dance_->DanceState()))) {
    if (manualPending_) {
      // A trusted maintenance transition must invalidate the runtime intent as
      // well as its transport retry, even if storage has already committed it.
      SelectionStep stopped;
      runtime_.Stop(stopped);
      steps_.OnStep(stopped);
    }
    return {CommandError::Busy, CommandOutcome::Rejected, false};
  }
  if (message.kind == LocalControlMessageKind::Reload) {
    InventoryReloadCommand command;
    command.operationEpoch = message.operationEpoch;
    command.commandId = message.commandId;
    command.deviceId = message.deviceId;
    command.bootId = message.bootId;
    command.controlLeaseId = message.controlLeaseId;
    command.sequence = message.sequence;
    command.expectedRevision = message.expectedRevision;
    command.issuedAtDeviceMs = message.issuedAtDeviceMs;
    command.maxAgeMs = message.maxAgeMs;
    command.compartment = message.compartment;
    command.inspectionConfirmed = true;
    return runtime_.ConfirmReload(command, ownsLease, acceptedAtMs);
  }
  SelectionCommand command;
  command.operationEpoch = message.operationEpoch;
  command.commandId = message.commandId;
  command.deviceId = message.deviceId;
  command.bootId = message.bootId;
  command.controlLeaseId = message.controlLeaseId;
  command.sequence = message.sequence;
  command.expectedRevision = message.expectedRevision;
  command.issuedAtDeviceMs = message.issuedAtDeviceMs;
  command.maxAgeMs = message.maxAgeMs;
  command.compartment = message.compartment;
  command.source = message.source;
  return runtime_.AcceptSelection(command, ownsLease, acceptedAtMs);
}

void LocalControlOwner::FinishManualCommand(
    const LocalControlMessage& message, const CommandReceipt& receipt,
    uint64_t nowMs) {
  // Transport stays outside the guard, and motion still requires its final ACK.
  if (!output_.CommandAck(handle_, message, receipt)) {
    Close();
    return;
  }
  if (message.kind == LocalControlMessageKind::Selection &&
      receipt.error == CommandError::None && !receipt.duplicate) {
    inbox_.RunIfCurrent(handle_, [&]() {
      SelectionStep step;
      const bool began = runtime_.BeginAcceptedSelection(clock_.NowMs(), step);
      if (began || step.effect == SelectionEffect::Stopped) {
        steps_.OnStep(step);
      }
    });
  }
  gateway_->OnControlTransition();
  Snapshot(nowMs);
}

bool LocalControlOwner::CompleteTurn(
    const std::string& clientTurnId, uint64_t generation) {
  return gateway_ && gateway_->CompleteTurn(clientTurnId, generation);
}

void LocalControlOwner::PumpReply(LocalConversationReplyPort& replies) {
  Poll();
  std::string reply;
  std::string conversationId;
  std::string clientTurnId;
  ConversationRuntimeSnapshot snapshot;
  uint64_t token = 0;
  if (!replies.TakeReply(reply, conversationId, clientTurnId, snapshot, token)) {
    WipeText(reply);
    return;
  }
  bool current = false;
  if (session_ && gateway_ && hello_) {
    inbox_.RunIfCurrent(handle_, [&]() {
      current = session_->OwnsLiveLease(bootId_, session_->LeaseId(), clock_.NowMs()) &&
                gateway_->IsActiveTurn(conversationId, clientTurnId, snapshot) &&
                replies.IsReplyCurrent(token, snapshot);
    });
  }
  // Transport work remains outside the urgent-event guard. Text is data only;
  // a result already queued before cancel is identified by its old generation.
  if (current && inbox_.CanProcess(handle_)) {
    if (!output_.ConversationResult(handle_, clientTurnId, snapshot.generation, reply)) {
      Close();
    } else {
      inbox_.RunIfCurrent(handle_, [&]() {
        if (replies.IsReplyCurrent(token, snapshot) &&
            gateway_->IsActiveTurn(conversationId, clientTurnId, snapshot)) {
          gateway_->CompleteTurn(clientTurnId, snapshot.generation);
        }
      });
      Snapshot(clock_.NowMs());
    }
  }
  replies.FinishReply(token);
  WipeText(reply);
}

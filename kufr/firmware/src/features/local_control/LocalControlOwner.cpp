#include "LocalControlOwner.h"

#include <new>
#include "../voice_turn/LocalVoicePort.h"

#include "LocalControlTransport.h"
#include "LocalConversationNetworkPort.h"
#include "LocalConversationReplyPort.h"
#include "../device_control/DeviceControlRuntime.h"

namespace {

void WipeText(std::string& text) {
  volatile char* bytes = text.empty() ? nullptr : &text[0];
  for (size_t index = 0; index < text.size(); ++index) {
    bytes[index] = 0;
  }
  text.clear();
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
          SelectionStep step;
          if (dance_) dance_->StopDance();
          if (voice_) voice_->StopVoice();
          runtime_.Stop(step);
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
  if (session_) {
    SelectionStep step;
    session_->Disconnect(step);
    steps_.OnStep(step);
    if (gateway_) {
      gateway_->OnControlTransition();
    }
  }
  gateway_.reset();
  session_.reset();
  handle_ = {};
  hello_ = false;
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
    if (!output_.Snapshot(handle_, session_->LeaseId(), nowMs)) {
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
  bool accepted = true;
  // Stop/cancel/status remain available while network/OTA owns the idle
  // reservation. A queued controller command cannot start new work then.
  const bool dancing = dance_ && kufr::dance::IsDanceActive(dance_->DanceState());
  if ((maintenance_ || dancing) && (message.kind == LocalControlMessageKind::Acquire ||
      message.kind == LocalControlMessageKind::VoiceStart ||
      message.kind == LocalControlMessageKind::Text ||
      message.kind == LocalControlMessageKind::Selection ||
      message.kind == LocalControlMessageKind::Reload ||
      message.kind == LocalControlMessageKind::Reset)) {
    Close();
    return;
  }
  switch (message.kind) {
    case LocalControlMessageKind::StateGet:
      Snapshot(nowMs);
      return;
    case LocalControlMessageKind::Acquire:
      if (voice_) voice_->StopVoice();
      accepted = session_->Acquire(bootId_, ids_.NewLeaseId(), nowMs);
      break;
    case LocalControlMessageKind::Release: {
      SelectionStep step;
      accepted = session_->Release(bootId_, message.controlLeaseId, step);
      if (accepted) {
        steps_.OnStep(step);
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
      accepted = output_.VoiceAck(handle_, message, !dancing && voice_ && voice_->ConfigureVoice(message.voiceMode, message.voice));
      break;
    case LocalControlMessageKind::VoiceStart: {
      ConversationRuntimeSnapshot snapshot;
      const bool ready = voice_ && session_->LeaseId().empty() && runtimeState_.Read(snapshot) &&
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
        started = dance_ && danceAdmission_ && !maintenance_ && !dancing && voiceIdle &&
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
    case LocalControlMessageKind::Reset:
      accepted = false;
      inbox_.RunIfCurrent(handle_, [&]() {
        accepted =
            session_->OwnsLiveLease(bootId_, message.controlLeaseId, clock_.NowMs()) &&
            recovery_.InspectionAllowsRecovery() &&
            runtime_.RecoverAfterInspection();
      });
      break;
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

void LocalControlOwner::Selection(
    const LocalControlMessage& message, uint64_t nowMs) {
  SelectionCommand command;
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
  CommandReceipt receipt;
  const bool current = inbox_.RunIfCurrent(handle_, [&]() {
    const uint64_t acceptedAtMs = clock_.NowMs();
    receipt = runtime_.AcceptSelection(
        command,
        session_->OwnsLiveLease(bootId_, message.controlLeaseId, acceptedAtMs),
        acceptedAtMs);
  });
  if (!current) {
    return;
  }
  if (!output_.CommandAck(handle_, message, receipt)) {
    Close();
    return;
  }
  if (receipt.error == CommandError::None && !receipt.duplicate) {
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

void LocalControlOwner::Reload(
    const LocalControlMessage& message, uint64_t nowMs) {
  InventoryReloadCommand command;
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
  CommandReceipt receipt;
  const bool current = inbox_.RunIfCurrent(handle_, [&]() {
    const uint64_t acceptedAtMs = clock_.NowMs();
    receipt = runtime_.ConfirmReload(
        command,
        session_->OwnsLiveLease(bootId_, message.controlLeaseId, acceptedAtMs),
        acceptedAtMs);
  });
  if (!current) {
    return;
  }
  if (!output_.CommandAck(handle_, message, receipt)) {
    Close();
    return;
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

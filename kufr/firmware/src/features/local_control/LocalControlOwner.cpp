#include "LocalControlOwner.h"

#include <new>

#include "LocalControlTransport.h"
#include "LocalConversationNetworkPort.h"
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
    LocalControlClockPort& clock)
    : deviceId_(deviceId), bootId_(bootId), runtime_(runtime),
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
  inbox_.RunIfNoUrgent([this]() {
    SelectionStep step;
    if (runtime_.Tick(clock_.NowMs(), step)) {
      steps_.OnStep(step);
    }
  });
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
  switch (message.kind) {
    case LocalControlMessageKind::StateGet:
      Snapshot(nowMs);
      return;
    case LocalControlMessageKind::Acquire:
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
    case LocalControlMessageKind::Text: {
      ConversationTextCommand command;
      command.bootId = message.bootId;
      command.conversationId = message.conversationId;
      command.clientTurnId = message.clientTurnId;
      command.generation = message.generation;
      command.expectedRevision = message.expectedRevision;
      command.text = message.text;
      const auto receipt = gateway_->SubmitText(
          command, message.controlLeaseId, nowMs);
      accepted = output_.ConversationAck(handle_, message, receipt);
      WipeText(command.text);
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

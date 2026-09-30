#include "LocalConversationGateway.h"

#include "LocalControlSession.h"
#include "LocalConversationNetworkPort.h"
#include "../conversation/ConversationRuntimeState.h"
#include "../device_control/DeviceControlRuntime.h"

LocalConversationGateway::LocalConversationGateway(
    LocalControlSession& localControl, DeviceControlRuntime& controlRuntime,
    ConversationRuntimeState& runtimeState, ConversationSession& conversation,
    LocalConversationNetworkPort& network)
    : localControl_(localControl),
      controlRuntime_(controlRuntime),
      runtimeState_(runtimeState),
      conversation_(conversation),
      network_(network) {}

ConversationCommandReceipt LocalConversationGateway::SubmitText(
    const ConversationTextCommand& command,
    const std::string& leaseId, uint64_t nowMs) {
  if (!localControl_.OwnsLiveLease(command.bootId, leaseId, nowMs)) {
    return {ConversationCommandError::Unauthorized, false};
  }
  if (!OnControlTransition()) {
    return {ConversationCommandError::WrongGeneration, false};
  }
  PollNetworkFailure();

  ConversationRuntimeSnapshot snapshot;
  if (!runtimeState_.Read(snapshot) ||
      snapshot.generation != controlRuntime_.Generation()) {
    return {ConversationCommandError::Busy, false};
  }
  const ConversationCommandReceipt validation =
      conversation_.ValidateText(command, true, snapshot.deviceRevision);
  if (validation.error != ConversationCommandError::None ||
      validation.duplicate) {
    return validation;
  }

  // Commit before publishing to the network task, then undo only a rejected
  // enqueue. The control owner cannot change this session during the call.
  const ConversationCommandReceipt accepted =
      conversation_.SubmitText(command, true, snapshot.deviceRevision);
  if (accepted.error != ConversationCommandError::None ||
      accepted.duplicate) {
    return accepted;
  }
  if (!network_.QueueTextTurn(command.text, command.conversationId,
                              command.clientTurnId, snapshot)) {
    conversation_.AbortUnqueuedTurn(command.clientTurnId,
                                    command.generation);
    return {ConversationCommandError::Busy, false};
  }
  activeTurnId_ = command.clientTurnId;
  activeSnapshotVersion_ = snapshot.version;
  return accepted;
}

ConversationCommandError LocalConversationGateway::Cancel(
    const std::string& bootId, const std::string& conversationId,
    const std::string& leaseId, uint64_t generation, uint64_t nowMs) {
  if (!localControl_.OwnsLiveLease(bootId, leaseId, nowMs)) {
    return ConversationCommandError::Unauthorized;
  }
  if (!OnControlTransition()) {
    return ConversationCommandError::WrongGeneration;
  }
  const ConversationCommandError validation =
      conversation_.ValidateCancel(
          bootId, conversationId, generation, true);
  if (validation != ConversationCommandError::None) {
    return validation;
  }
  ConversationRuntimeSnapshot snapshot;
  if (!runtimeState_.Read(snapshot) ||
      snapshot.generation != generation) {
    return ConversationCommandError::Busy;
  }
  if (!controlRuntime_.CancelConversation(leaseId, generation, nowMs)) {
    return ConversationCommandError::Unauthorized;
  }
  const ConversationCommandError result =
      conversation_.Cancel(bootId, conversationId, generation, true);
  activeTurnId_.clear();
  activeSnapshotVersion_ = 0;
  network_.QueueCancelFor(conversationId, snapshot.version);
  return result;
}

bool LocalConversationGateway::CompleteTurn(
    const std::string& clientTurnId, uint64_t generation) {
  if (!OnControlTransition() || clientTurnId != activeTurnId_ ||
      !conversation_.CompleteTurn(clientTurnId, generation)) {
    return false;
  }
  activeTurnId_.clear();
  activeSnapshotVersion_ = 0;
  return true;
}

bool LocalConversationGateway::IsActiveTurn(
    const std::string& conversationId, const std::string& clientTurnId,
    const ConversationRuntimeSnapshot& snapshot) {
  return OnControlTransition() && conversationId == conversation_.ConversationId() &&
         !activeTurnId_.empty() && clientTurnId == activeTurnId_ &&
         clientTurnId == conversation_.ActiveTurnId() &&
         snapshot.generation == conversation_.Generation() &&
         snapshot.version == activeSnapshotVersion_ && runtimeState_.Matches(snapshot);
}

bool LocalConversationGateway::PollNetworkFailure() {
  if (!OnControlTransition() || activeTurnId_.empty() ||
      !network_.TakeFailedTurnFor(
          conversation_.ConversationId(), activeTurnId_,
          activeSnapshotVersion_) ||
      !conversation_.CompleteTurn(activeTurnId_,
                                  conversation_.Generation())) {
    return false;
  }
  activeTurnId_.clear();
  activeSnapshotVersion_ = 0;
  return true;
}

bool LocalConversationGateway::OnControlTransition() {
  const uint64_t previousGeneration = conversation_.Generation();
  if (!conversation_.SynchronizeGeneration(controlRuntime_.Generation())) {
    return false;
  }
  const bool generationChanged =
      conversation_.Generation() != previousGeneration;
  ConversationRuntimeSnapshot snapshot;
  const bool snapshotChanged =
      !activeTurnId_.empty() &&
      (!runtimeState_.Read(snapshot) ||
       snapshot.version != activeSnapshotVersion_);
  if (generationChanged || snapshotChanged) {
    const bool hadActiveTurn = !activeTurnId_.empty();
    const uint64_t canceledSnapshotVersion = activeSnapshotVersion_;
    if (snapshotChanged && !generationChanged) {
      conversation_.CompleteTurn(activeTurnId_,
                                  conversation_.Generation());
    }
    activeTurnId_.clear();
    activeSnapshotVersion_ = 0;
    if (hadActiveTurn) {
      network_.QueueCancelFor(
          conversation_.ConversationId(), canceledSnapshotVersion);
    }
  }
  return true;
}

#pragma once

#include <stdint.h>

#include <string>

#include "../conversation/ConversationSession.h"

class ConversationRuntimeState;
class DeviceControlRuntime;
class LocalControlSession;
class LocalConversationNetworkPort;

// Called only on the control owner task for one already authenticated socket.
class LocalConversationGateway final {
 public:
  LocalConversationGateway(
      LocalControlSession& localControl, DeviceControlRuntime& controlRuntime,
      ConversationRuntimeState& runtimeState, ConversationSession& conversation,
      LocalConversationNetworkPort& network);

  ConversationCommandReceipt SubmitText(
      const ConversationTextCommand& command,
      const std::string& leaseId, uint64_t nowMs);
  ConversationCommandError Cancel(
      const std::string& bootId, const std::string& conversationId,
      const std::string& leaseId, uint64_t generation, uint64_t nowMs);
  bool CompleteTurn(const std::string& clientTurnId, uint64_t generation);
  bool PollNetworkFailure();
  bool OnControlTransition();

 private:
  LocalControlSession& localControl_;
  DeviceControlRuntime& controlRuntime_;
  ConversationRuntimeState& runtimeState_;
  ConversationSession& conversation_;
  LocalConversationNetworkPort& network_;
  std::string activeTurnId_;
  uint64_t activeSnapshotVersion_ = 0;
};

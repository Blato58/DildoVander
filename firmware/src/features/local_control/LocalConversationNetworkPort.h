#pragma once

#include <stdint.h>

#include <string>

#include "../conversation/ConversationRuntimeState.h"

class LocalConversationNetworkPort {
 public:
  virtual ~LocalConversationNetworkPort() = default;

  virtual bool QueueTextTurn(
      const std::string& text, const std::string& conversationId,
      const std::string& clientTurnId,
      const ConversationRuntimeSnapshot& expectedSnapshot) = 0;
  virtual bool QueueCancelFor(
      const std::string& conversationId, uint64_t snapshotVersion) = 0;
  virtual bool TakeFailedTurnFor(
      const std::string& conversationId,
      const std::string& clientTurnId, uint64_t snapshotVersion) = 0;
};

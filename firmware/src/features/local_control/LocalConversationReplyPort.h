#pragma once

#include "../conversation/ConversationRuntimeState.h"

// Data handoff only. The control owner releases every token it takes.
class LocalConversationReplyPort {
 public:
  virtual ~LocalConversationReplyPort() = default;
  virtual bool TakeReply(std::string& reply, std::string& conversationId,
                         std::string& clientTurnId,
                         ConversationRuntimeSnapshot& snapshot,
                         uint64_t& token) = 0;
  virtual bool IsReplyCurrent(
      uint64_t token, const ConversationRuntimeSnapshot& snapshot) const = 0;
  virtual bool FinishReply(uint64_t token) = 0;
};

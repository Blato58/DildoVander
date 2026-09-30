#pragma once

#include <cstdint>
#include <mutex>
#include <string>

#include "ConversationRuntimeState.h"

// A single validated backend reply awaiting a local output owner. It is data
// only; taking a reply never starts audio or physical presentation.
class ConversationReplyInbox final {
 public:
  bool Store(const std::string& reply, const std::string& clientTurnId,
             const ConversationRuntimeSnapshot& expectedSnapshot,
             const ConversationRuntimeState& runtimeState);
  bool Take(std::string& reply, std::string& clientTurnId,
            ConversationRuntimeSnapshot& snapshot,
            uint64_t& token,
            const ConversationRuntimeState& runtimeState);
  bool IsCurrent(uint64_t token,
                 const ConversationRuntimeSnapshot& snapshot,
                 const ConversationRuntimeState& runtimeState) const;
  bool Complete(uint64_t token);
  bool HasPending(const ConversationRuntimeState& runtimeState);
  void Clear();

 private:
  void InvalidateLocked();

  mutable std::mutex mutex_;
  std::string pendingReply_;
  std::string pendingClientTurnId_;
  ConversationRuntimeSnapshot snapshot_;
  uint64_t token_ = 1;
  bool inFlight_ = false;
};

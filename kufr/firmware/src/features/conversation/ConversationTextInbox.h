#pragma once

#include <stdint.h>

#include <mutex>
#include <string>

#include "ConversationRuntimeState.h"

// One explicit text event. Its client turn ID stays with the backend request.
class ConversationTextInbox final {
 public:
  bool Submit(const std::string& text, const std::string& clientTurnId,
              const ConversationRuntimeSnapshot& expectedSnapshot,
              const ConversationRuntimeState& runtimeState);
  bool Take(std::string& text, std::string& clientTurnId,
            uint64_t& snapshotVersion);
  void Clear();

 private:
  std::mutex mutex_;
  std::string pendingText_;
  std::string pendingClientTurnId_;
  uint64_t pendingVersion_ = 0;
};

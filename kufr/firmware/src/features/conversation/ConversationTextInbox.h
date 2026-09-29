#pragma once

#include <stdint.h>

#include <mutex>
#include <string>

#include "ConversationRuntimeState.h"

// One explicit development text event. No event is replayed after disconnect.
class ConversationTextInbox final {
 public:
  bool Submit(const std::string& text,
              const ConversationRuntimeSnapshot& expectedSnapshot,
              const ConversationRuntimeState& runtimeState);
  bool Take(std::string& text, uint64_t& snapshotVersion);
  void Clear();

 private:
  std::mutex mutex_;
  std::string pendingText_;
  uint64_t pendingVersion_ = 0;
};

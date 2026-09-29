#pragma once

#include <stdint.h>

#include <mutex>
#include <string>

// One explicit development text event. No event is replayed after disconnect.
class ConversationTextInbox final {
 public:
  bool Submit(const std::string& text, uint64_t snapshotVersion);
  bool Take(std::string& text, uint64_t& snapshotVersion);
  void Clear();

 private:
  std::mutex mutex_;
  std::string pendingText_;
  uint64_t pendingVersion_ = 0;
};

#pragma once

#include <mutex>
#include <string>

// One explicit development text event. No event is replayed after disconnect.
class ConversationTextInbox final {
 public:
  bool Submit(const std::string& text);
  bool Take(std::string& text);
  void Clear();

 private:
  std::mutex mutex_;
  std::string pendingText_;
};

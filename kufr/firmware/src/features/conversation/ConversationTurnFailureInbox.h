#pragma once

#include <stdint.h>
#include <stddef.h>

#include <array>
#include <mutex>
#include <string>

// Network task hands bounded terminal turn failures to the control owner.
// A failure keeps the turn ID spent because the backend may have accepted it.
class ConversationTurnFailureInbox final {
 public:
  void Store(const std::string& conversationId,
             const std::string& clientTurnId, uint64_t snapshotVersion);
  bool TakeFor(const std::string& conversationId,
               const std::string& clientTurnId, uint64_t snapshotVersion);
  void Clear();

 private:
  struct Failure {
    std::string conversationId;
    std::string clientTurnId;
    uint64_t snapshotVersion = 0;
  };

  static constexpr size_t MaxFailures = 4;
  std::mutex mutex_;
  std::array<Failure, MaxFailures> failures_;
  size_t count_ = 0;
};

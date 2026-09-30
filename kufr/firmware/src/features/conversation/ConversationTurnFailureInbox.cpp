#include "ConversationTurnFailureInbox.h"

#include <utility>

void ConversationTurnFailureInbox::Store(
    const std::string& conversationId, const std::string& clientTurnId,
    uint64_t snapshotVersion) {
  if (conversationId.empty() || clientTurnId.empty() ||
      snapshotVersion == 0) {
    return;
  }
  std::lock_guard<std::mutex> lock(mutex_);
  for (size_t index = 0; index < count_; ++index) {
    const Failure& failure = failures_[index];
    if (failure.conversationId == conversationId &&
        failure.clientTurnId == clientTurnId &&
        failure.snapshotVersion == snapshotVersion) {
      return;
    }
  }
  if (count_ == MaxFailures) {
    for (size_t index = 1; index < count_; ++index) {
      failures_[index - 1] = std::move(failures_[index]);
    }
    --count_;
  }
  failures_[count_].conversationId = conversationId;
  failures_[count_].clientTurnId = clientTurnId;
  failures_[count_].snapshotVersion = snapshotVersion;
  ++count_;
}

bool ConversationTurnFailureInbox::TakeFor(
    const std::string& conversationId, const std::string& clientTurnId,
    uint64_t snapshotVersion) {
  std::lock_guard<std::mutex> lock(mutex_);
  for (size_t index = 0; index < count_; ++index) {
    const Failure& failure = failures_[index];
    if (failure.conversationId != conversationId ||
        failure.clientTurnId != clientTurnId ||
        failure.snapshotVersion != snapshotVersion) {
      continue;
    }
    for (size_t shift = index + 1; shift < count_; ++shift) {
      failures_[shift - 1] = std::move(failures_[shift]);
    }
    --count_;
    failures_[count_] = {};
    return true;
  }
  return false;
}

void ConversationTurnFailureInbox::Clear() {
  std::lock_guard<std::mutex> lock(mutex_);
  for (size_t index = 0; index < count_; ++index) {
    failures_[index] = {};
  }
  count_ = 0;
}

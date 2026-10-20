#include "ConversationReplyInbox.h"

#include <algorithm>

#include "ConversationSession.h"
#include "ConversationStartCodec.h"

bool ConversationReplyInbox::Store(
    const std::string& reply, const std::string& conversationId,
    const std::string& clientTurnId,
    const ConversationRuntimeSnapshot& expectedSnapshot,
    const ConversationRuntimeState& runtimeState) {
  std::string normalizedId;
  if (expectedSnapshot.version == 0 || reply.size() > 2000 ||
      !IsValidConversationUtf8(reply, 500, false) ||
      !NormalizeConversationUuid(conversationId, normalizedId) ||
      !NormalizeConversationUuid(clientTurnId, normalizedId)) {
    return false;
  }

  std::lock_guard<std::mutex> lock(mutex_);
  if (!pendingReply_.empty() || inFlight_ || token_ == 0 ||
      token_ == UINT64_MAX || !runtimeState.Matches(expectedSnapshot)) {
    return false;
  }
  ++token_;
  pendingReply_ = reply;
  pendingConversationId_ = conversationId;
  pendingClientTurnId_ = clientTurnId;
  snapshot_ = expectedSnapshot;
  return true;
}

bool ConversationReplyInbox::Take(
    std::string& reply, std::string& conversationId,
    std::string& clientTurnId,
    ConversationRuntimeSnapshot& snapshot,
    uint64_t& token,
    const ConversationRuntimeState& runtimeState) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (pendingReply_.empty()) {
    return false;
  }
  if (!runtimeState.Matches(snapshot_)) {
    InvalidateLocked();
    return false;
  }
  reply = pendingReply_;
  conversationId = pendingConversationId_;
  clientTurnId = pendingClientTurnId_;
  snapshot = snapshot_;
  token = token_;
  std::fill(pendingReply_.begin(), pendingReply_.end(), '\0');
  pendingReply_.clear();
  pendingConversationId_.clear();
  pendingClientTurnId_.clear();
  inFlight_ = true;
  return true;
}

bool ConversationReplyInbox::IsCurrent(
    uint64_t token, const ConversationRuntimeSnapshot& snapshot,
    const ConversationRuntimeState& runtimeState) const {
  std::lock_guard<std::mutex> lock(mutex_);
  return token != 0 && inFlight_ && token == token_ &&
         snapshot.version == snapshot_.version &&
         runtimeState.Matches(snapshot) && runtimeState.Matches(snapshot_);
}

bool ConversationReplyInbox::Complete(uint64_t token) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (token == 0 || !inFlight_ || token != token_) {
    return false;
  }
  InvalidateLocked();
  return true;
}

bool ConversationReplyInbox::HasPending(
    const ConversationRuntimeState& runtimeState) {
  std::lock_guard<std::mutex> lock(mutex_);
  if ((!pendingReply_.empty() || inFlight_) &&
      !runtimeState.Matches(snapshot_)) {
    InvalidateLocked();
  }
  return !pendingReply_.empty() || inFlight_;
}

void ConversationReplyInbox::Clear() {
  std::lock_guard<std::mutex> lock(mutex_);
  InvalidateLocked();
}

void ConversationReplyInbox::InvalidateLocked() {
  std::fill(pendingReply_.begin(), pendingReply_.end(), '\0');
  pendingReply_.clear();
  pendingConversationId_.clear();
  pendingClientTurnId_.clear();
  snapshot_ = {};
  inFlight_ = false;
  token_ = token_ == UINT64_MAX ? 0 : token_ + 1;
}

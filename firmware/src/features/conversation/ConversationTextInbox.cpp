#include "ConversationTextInbox.h"

#include <algorithm>
#include <utility>

#include "ConversationSession.h"
#include "ConversationStartCodec.h"

bool ConversationTextInbox::Submit(const std::string& text,
                                   const std::string& conversationId,
                                   const std::string& clientTurnId,
                                   const ConversationRuntimeSnapshot&
                                       expectedSnapshot,
                                   const ConversationRuntimeState& runtimeState) {
  std::string normalizedId;
  if (expectedSnapshot.version == 0 || text.size() > 4000 ||
      !IsValidConversationText(text) ||
      !NormalizeConversationUuid(conversationId, normalizedId) ||
      !NormalizeConversationUuid(clientTurnId, normalizedId)) {
    return false;
  }
  bool hasVisibleContent = false;
  for (unsigned char character : text) {
    if (character > 0x7f ||
        (character != ' ' && character != '\t' && character != '\r' &&
         character != '\n')) {
      hasVisibleContent = true;
      break;
    }
  }
  if (!hasVisibleContent) {
    return false;
  }

  std::lock_guard<std::mutex> lock(mutex_);
  // Recheck at admission: a control transition may invalidate the snapshot
  // while the producer validates text or waits for the inbox lock.
  if (!pendingText_.empty() || !runtimeState.Matches(expectedSnapshot)) {
    return false;
  }
  pendingText_ = text;
  pendingConversationId_ = conversationId;
  pendingClientTurnId_ = clientTurnId;
  pendingVersion_ = expectedSnapshot.version;
  return true;
}

bool ConversationTextInbox::Take(std::string& text,
                                 std::string& conversationId,
                                 std::string& clientTurnId,
                                 uint64_t& snapshotVersion) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (pendingText_.empty()) {
    return false;
  }
  text = std::move(pendingText_);
  conversationId = std::move(pendingConversationId_);
  clientTurnId = std::move(pendingClientTurnId_);
  snapshotVersion = pendingVersion_;
  pendingText_.clear();
  pendingConversationId_.clear();
  pendingClientTurnId_.clear();
  pendingVersion_ = 0;
  return true;
}

void ConversationTextInbox::Clear() {
  std::lock_guard<std::mutex> lock(mutex_);
  std::fill(pendingText_.begin(), pendingText_.end(), '\0');
  pendingText_.clear();
  pendingConversationId_.clear();
  pendingClientTurnId_.clear();
  pendingVersion_ = 0;
}

bool ConversationTextInbox::DiscardUnless(
    const std::string& conversationId,
    std::string* discardedConversationId,
    std::string* discardedClientTurnId,
    uint64_t* discardedSnapshotVersion) {
  std::lock_guard<std::mutex> lock(mutex_);
  std::string normalizedExpected;
  std::string normalizedPending;
  if (pendingText_.empty() ||
      (NormalizeConversationUuid(conversationId, normalizedExpected) &&
       NormalizeConversationUuid(pendingConversationId_, normalizedPending) &&
       normalizedExpected == normalizedPending)) {
    return false;
  }
  if (discardedConversationId != nullptr) {
    *discardedConversationId = pendingConversationId_;
  }
  if (discardedClientTurnId != nullptr) {
    *discardedClientTurnId = pendingClientTurnId_;
  }
  if (discardedSnapshotVersion != nullptr) {
    *discardedSnapshotVersion = pendingVersion_;
  }
  std::fill(pendingText_.begin(), pendingText_.end(), '\0');
  pendingText_.clear();
  pendingConversationId_.clear();
  pendingClientTurnId_.clear();
  pendingVersion_ = 0;
  return true;
}

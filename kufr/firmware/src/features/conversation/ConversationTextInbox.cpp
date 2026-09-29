#include "ConversationTextInbox.h"

#include <algorithm>
#include <utility>

#include "ConversationSession.h"

bool ConversationTextInbox::Submit(const std::string& text,
                                   const ConversationRuntimeSnapshot& expectedSnapshot,
                                   const ConversationRuntimeState& runtimeState) {
  if (expectedSnapshot.version == 0 || text.size() > 4000 ||
      !IsValidConversationText(text)) {
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
  pendingVersion_ = expectedSnapshot.version;
  return true;
}

bool ConversationTextInbox::Take(std::string& text,
                                 uint64_t& snapshotVersion) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (pendingText_.empty()) {
    return false;
  }
  text = std::move(pendingText_);
  snapshotVersion = pendingVersion_;
  pendingText_.clear();
  pendingVersion_ = 0;
  return true;
}

void ConversationTextInbox::Clear() {
  std::lock_guard<std::mutex> lock(mutex_);
  std::fill(pendingText_.begin(), pendingText_.end(), '\0');
  pendingText_.clear();
  pendingVersion_ = 0;
}

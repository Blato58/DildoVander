#include "ConversationTextInbox.h"

#include <algorithm>
#include <utility>

#include "ConversationSession.h"

bool ConversationTextInbox::Submit(const std::string& text) {
  if (text.size() > 4000 || !IsValidConversationText(text)) {
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
  if (!pendingText_.empty()) {
    return false;
  }
  pendingText_ = text;
  return true;
}

bool ConversationTextInbox::Take(std::string& text) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (pendingText_.empty()) {
    return false;
  }
  text = std::move(pendingText_);
  pendingText_.clear();
  return true;
}

void ConversationTextInbox::Clear() {
  std::lock_guard<std::mutex> lock(mutex_);
  std::fill(pendingText_.begin(), pendingText_.end(), '\0');
  pendingText_.clear();
}

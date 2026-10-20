#include "ConversationSession.h"

#include <algorithm>

bool IsValidConversationUtf8(const std::string& text,
                             size_t maximumCharacters,
                             bool allowEmpty) {
  size_t characters = 0;
  for (size_t index = 0; index < text.size();) {
    const unsigned char first = static_cast<unsigned char>(text[index]);
    size_t width = 0;
    if (first <= 0x7f) {
      width = 1;
    } else if (first >= 0xc2 && first <= 0xdf) {
      width = 2;
    } else if (first >= 0xe0 && first <= 0xef) {
      width = 3;
    } else if (first >= 0xf0 && first <= 0xf4) {
      width = 4;
    } else {
      return false;
    }
    if (index + width > text.size()) {
      return false;
    }
    for (size_t offset = 1; offset < width; ++offset) {
      const unsigned char next =
          static_cast<unsigned char>(text[index + offset]);
      if ((next & 0xc0) != 0x80) {
        return false;
      }
    }
    if (width >= 3) {
      const unsigned char second =
          static_cast<unsigned char>(text[index + 1]);
      if ((first == 0xe0 && second < 0xa0) ||
          (first == 0xed && second >= 0xa0) ||
          (first == 0xf0 && second < 0x90) ||
          (first == 0xf4 && second >= 0x90)) {
        return false;
      }
    }
    ++characters;
    if (characters > maximumCharacters) {
      return false;
    }
    index += width;
  }
  return allowEmpty || characters > 0;
}

bool IsValidConversationText(const std::string& text) {
  return IsValidConversationUtf8(text, 1000, false);
}

ConversationSession::ConversationSession(const std::string& bootId,
                                         const std::string& conversationId)
    : bootId_(bootId), conversationId_(conversationId) {}

ConversationCommandReceipt ConversationSession::ValidateText(
    const ConversationTextCommand& command, bool manualLeaseValid,
    uint64_t currentRevision) const {
  if (!manualLeaseValid) {
    return {ConversationCommandError::Unauthorized, false};
  }
  if (command.bootId != bootId_) {
    return {ConversationCommandError::WrongBoot, false};
  }
  if (command.conversationId != conversationId_) {
    return {ConversationCommandError::WrongSession, false};
  }
  if (command.generation != generation_) {
    return {ConversationCommandError::WrongGeneration, false};
  }
  if (command.clientTurnId.empty() || command.text.size() > 8192 ||
      !IsValidConversationText(command.text)) {
    return {ConversationCommandError::InvalidText, false};
  }
  if (HasActiveTurn()) {
    if (command.clientTurnId != clientTurnId_) {
      return {ConversationCommandError::Busy, false};
    }
    return command.text == currentText_
               ? ConversationCommandReceipt{ConversationCommandError::None, true}
               : ConversationCommandReceipt{ConversationCommandError::IdConflict,
                                            false};
  }
  for (size_t index = 0; index < usedTurnCount_; ++index) {
    if (usedTurnIds_[index] == command.clientTurnId) {
      return {ConversationCommandError::TurnAlreadySeen, false};
    }
  }
  if (usedTurnCount_ == MaxTurns) {
    return {ConversationCommandError::TurnLimitReached, false};
  }
  if (command.expectedRevision != currentRevision) {
    return {ConversationCommandError::StaleRevision, false};
  }

  return {ConversationCommandError::None, false};
}

ConversationCommandReceipt ConversationSession::SubmitText(
    const ConversationTextCommand& command, bool manualLeaseValid,
    uint64_t currentRevision) {
  const ConversationCommandReceipt receipt =
      ValidateText(command, manualLeaseValid, currentRevision);
  if (receipt.error != ConversationCommandError::None || receipt.duplicate) {
    return receipt;
  }

  clientTurnId_ = command.clientTurnId;
  currentText_ = command.text;
  usedTurnIds_[usedTurnCount_] = command.clientTurnId;
  ++usedTurnCount_;
  return {ConversationCommandError::None, false};
}

ConversationCommandError ConversationSession::ValidateCancel(
    const std::string& bootId, const std::string& conversationId,
    uint64_t generation, bool manualLeaseValid) const {
  if (!manualLeaseValid) {
    return ConversationCommandError::Unauthorized;
  }
  if (bootId != bootId_) {
    return ConversationCommandError::WrongBoot;
  }
  if (conversationId != conversationId_) {
    return ConversationCommandError::WrongSession;
  }
  if (generation != generation_) {
    return ConversationCommandError::WrongGeneration;
  }

  return ConversationCommandError::None;
}

ConversationCommandError ConversationSession::Cancel(
    const std::string& bootId, const std::string& conversationId,
    uint64_t generation, bool manualLeaseValid) {
  const ConversationCommandError error =
      ValidateCancel(bootId, conversationId, generation, manualLeaseValid);
  if (error != ConversationCommandError::None) {
    return error;
  }

  ClearSensitiveTurn();
  ++generation_;
  return ConversationCommandError::None;
}

bool ConversationSession::CompleteTurn(const std::string& clientTurnId,
                                       uint64_t generation) {
  if (!HasActiveTurn() || generation != generation_ ||
      clientTurnId != clientTurnId_) {
    return false;
  }
  ClearSensitiveTurn();
  return true;
}

void ConversationSession::LoseManualLease() {
  ClearSensitiveTurn();
  ++generation_;
}

bool ConversationSession::SynchronizeGeneration(uint64_t generation) {
  if (generation < generation_) {
    return false;
  }
  if (generation > generation_) {
    ClearSensitiveTurn();
    generation_ = generation;
  }
  return true;
}

bool ConversationSession::AbortUnqueuedTurn(
    const std::string& clientTurnId, uint64_t generation) {
  if (!HasActiveTurn() || generation != generation_ ||
      clientTurnId != clientTurnId_ || usedTurnCount_ == 0 ||
      usedTurnIds_[usedTurnCount_ - 1] != clientTurnId) {
    return false;
  }
  ClearSensitiveTurn();
  usedTurnIds_[usedTurnCount_ - 1].clear();
  --usedTurnCount_;
  return true;
}

uint64_t ConversationSession::Generation() const { return generation_; }

bool ConversationSession::HasActiveTurn() const {
  return !clientTurnId_.empty();
}

const std::string& ConversationSession::ConversationId() const {
  return conversationId_;
}

const std::string& ConversationSession::ActiveTurnId() const { return clientTurnId_; }

void ConversationSession::ClearSensitiveTurn() {
  std::fill(currentText_.begin(), currentText_.end(), '\0');
  currentText_.clear();
  clientTurnId_.clear();
}

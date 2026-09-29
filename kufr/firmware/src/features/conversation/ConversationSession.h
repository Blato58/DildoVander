#pragma once

#include <stdint.h>
#include <stddef.h>

#include <array>
#include <string>

bool IsValidConversationText(const std::string& text);

enum class ConversationCommandError : uint8_t {
  None,
  Unauthorized,
  WrongBoot,
  WrongSession,
  WrongGeneration,
  StaleRevision,
  InvalidText,
  Busy,
  IdConflict,
  TurnAlreadySeen,
  TurnLimitReached,
};

struct ConversationTextCommand {
  std::string bootId;
  std::string conversationId;
  std::string clientTurnId;
  uint64_t generation = 0;
  uint64_t expectedRevision = 0;
  std::string text;
};

struct ConversationCommandReceipt {
  ConversationCommandReceipt(
      ConversationCommandError error = ConversationCommandError::None,
      bool duplicate = false)
      : error(error), duplicate(duplicate) {}

  ConversationCommandError error;
  bool duplicate;
};

// Owns one device conversation's generation and current text turn. The caller
// authenticates the device/manual lease and forwards accepted text to an adapter.
class ConversationSession {
 public:
  static constexpr size_t MaxTurns = 10;

  ConversationSession(const std::string& bootId,
                      const std::string& conversationId);

  ConversationCommandReceipt SubmitText(const ConversationTextCommand& command,
                                        bool manualLeaseValid,
                                        uint64_t currentRevision);
  ConversationCommandError Cancel(const std::string& bootId,
                                  const std::string& conversationId,
                                  uint64_t generation, bool manualLeaseValid);
  bool CompleteTurn(const std::string& clientTurnId, uint64_t generation);
  void LoseManualLease();

  uint64_t Generation() const;
  bool HasActiveTurn() const;
  const std::string& ConversationId() const;

 private:
  void ClearSensitiveTurn();

  std::string bootId_;
  std::string conversationId_;
  uint64_t generation_ = 0;
  std::string clientTurnId_;
  std::string currentText_;
  std::array<std::string, MaxTurns> usedTurnIds_;
  size_t usedTurnCount_ = 0;
};

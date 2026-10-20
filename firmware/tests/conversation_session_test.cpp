#include <assert.h>

#include <string>

#include "../src/features/conversation/ConversationSession.h"

namespace {

ConversationTextCommand TextCommand(const std::string& turnId,
                                    uint64_t generation,
                                    const std::string& text) {
  ConversationTextCommand command;
  command.bootId = "boot-1";
  command.conversationId = "conversation-1";
  command.clientTurnId = turnId;
  command.generation = generation;
  command.expectedRevision = 4;
  command.text = text;
  return command;
}

void TestDuplicateAndCancellation() {
  ConversationSession session("boot-1", "conversation-1");
  const ConversationTextCommand first = TextCommand("turn-1", 0, "hello");
  assert(session.SubmitText(first, true, 4).error ==
         ConversationCommandError::None);
  assert(session.HasActiveTurn());
  assert(session.SubmitText(first, true, 5).duplicate);

  ConversationTextCommand conflicting = first;
  conflicting.text = "different";
  assert(session.SubmitText(conflicting, true, 5).error ==
         ConversationCommandError::IdConflict);
  assert(session.SubmitText(TextCommand("turn-2", 0, "another"), true, 4).error ==
         ConversationCommandError::Busy);

  assert(session.Cancel("boot-1", "conversation-1", 0, true) ==
         ConversationCommandError::None);
  assert(session.Generation() == 1);
  assert(!session.HasActiveTurn());
  assert(!session.CompleteTurn("turn-1", 0));
  assert(session.SubmitText(first, true, 4).error ==
         ConversationCommandError::WrongGeneration);
  assert(session.SubmitText(TextCommand("turn-1", 1, "hello"), true, 4).error ==
         ConversationCommandError::TurnAlreadySeen);
  assert(session.SubmitText(TextCommand("turn-2", 1, "next"), true, 4).error ==
         ConversationCommandError::None);
  assert(session.CompleteTurn("turn-2", 1));
  assert(!session.HasActiveTurn());
  assert(session.SubmitText(TextCommand("turn-2", 1, "next"), true, 4).error ==
         ConversationCommandError::TurnAlreadySeen);
}

void TestIdentityLeaseAndRevision() {
  ConversationSession session("boot-1", "conversation-1");
  ConversationTextCommand command = TextCommand("turn-1", 0, "hello");
  assert(session.SubmitText(command, false, 4).error ==
         ConversationCommandError::Unauthorized);
  command.bootId = "old-boot";
  assert(session.SubmitText(command, true, 4).error ==
         ConversationCommandError::WrongBoot);
  command.bootId = "boot-1";
  command.conversationId = "other";
  assert(session.SubmitText(command, true, 4).error ==
         ConversationCommandError::WrongSession);
  command.conversationId = "conversation-1";
  assert(session.SubmitText(command, true, 5).error ==
         ConversationCommandError::StaleRevision);
  assert(!session.HasActiveTurn());
  assert(session.Cancel("boot-1", "conversation-1", 0, false) ==
         ConversationCommandError::Unauthorized);

  assert(session.SubmitText(command, true, 4).error ==
         ConversationCommandError::None);
  session.LoseManualLease();
  assert(session.Generation() == 1);
  assert(!session.HasActiveTurn());
  assert(!session.CompleteTurn("turn-1", 0));
}

void TestUtf8AndCharacterLimit() {
  ConversationSession session("boot-1", "conversation-1");
  ConversationTextCommand command = TextCommand("turn-1", 0, "");
  assert(session.SubmitText(command, true, 4).error ==
         ConversationCommandError::InvalidText);
  command.text = std::string(1001, 'a');
  assert(session.SubmitText(command, true, 4).error ==
         ConversationCommandError::InvalidText);
  command.text = std::string("\xc4\x8d") + std::string(999, 'a');
  assert(session.SubmitText(command, true, 4).error ==
         ConversationCommandError::None);
  assert(session.Cancel("boot-1", "conversation-1", 0, true) ==
         ConversationCommandError::None);

  command = TextCommand("turn-2", 1, "\xc0\xaf");
  assert(session.SubmitText(command, true, 4).error ==
         ConversationCommandError::InvalidText);
}

void TestTurnLimit() {
  ConversationSession session("boot-1", "conversation-1");
  for (size_t index = 0; index < ConversationSession::MaxTurns; ++index) {
    const ConversationTextCommand command =
        TextCommand("turn-" + std::to_string(index), 0, "hello");
    assert(session.SubmitText(command, true, 4).error ==
           ConversationCommandError::None);
    assert(session.CompleteTurn(command.clientTurnId, 0));
  }
  assert(session.SubmitText(TextCommand("turn-10", 0, "next"), true, 4).error ==
         ConversationCommandError::TurnLimitReached);
}

}  // namespace

int main() {
  TestDuplicateAndCancellation();
  TestIdentityLeaseAndRevision();
  TestUtf8AndCharacterLimit();
  TestTurnLimit();
}

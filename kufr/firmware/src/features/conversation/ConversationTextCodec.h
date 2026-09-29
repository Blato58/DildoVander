#pragma once

#include <stdint.h>

#include <string>

struct ConversationTextTurnRequest {
  std::string conversationId;
  std::string clientTurnId;
  std::string bootId;
  std::string idempotencyKey;
  std::string catalogVersion;
  std::string text;
  uint64_t generation = 0;
  uint64_t deviceRevision = 0;
  uint8_t readyMask = 0;
};

// Constructs the exact JSON body for POST /v1/conversations/{id}/turns.
bool BuildConversationTextTurnRequest(
    const ConversationTextTurnRequest& request, std::string& body);

#pragma once

#include <stddef.h>
#include <stdint.h>

#include <string>

struct ConversationStartRequest {
  std::string deviceId;
  std::string bootId;
  std::string idempotencyKey;
  std::string catalogVersion;
  uint64_t generation = 0;
  bool audioConsent = false;
  bool imageConsent = false;
  uint64_t operationEpoch = 0;
  // RAM-only; never copied into an operation journal or diagnostic event.
  std::string testContext;
};

struct ConversationStartResponse {
  std::string conversationId;
  std::string expiresAt;
};

bool IsConversationUuid(const std::string& value);
// Local control UUIDs are case-insensitive; backend correlation uses lowercase.
// Rejects the nil UUID, which the backend does not accept as a turn ID.
bool NormalizeConversationUuid(const std::string& value,
                               std::string& normalized);

// Consents are explicit for this conversation, never inherited from pairing.
bool BuildConversationStartRequest(const ConversationStartRequest& request,
                                   std::string& body);
bool ParseConversationStartResponse(const char* data, size_t length,
                                    ConversationStartResponse& response);

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

// Audio consent is explicit for an operator-started voice session; image stays false.
bool BuildConversationStartRequest(const ConversationStartRequest& request,
                                   std::string& body);
bool ParseConversationStartResponse(const char* data, size_t length,
                                    ConversationStartResponse& response);

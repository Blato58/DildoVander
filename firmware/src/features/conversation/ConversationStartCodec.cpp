#include "ConversationStartCodec.h"
#include "../voice_turn/VoiceTestContext.h"

#include <limits>

bool IsConversationUuid(const std::string& value) {
  if (value.size() != 36) {
    return false;
  }
  for (size_t index = 0; index < value.size(); ++index) {
    const char current = value[index];
    if (index == 8 || index == 13 || index == 18 || index == 23) {
      if (current != '-') {
        return false;
      }
    } else if (!((current >= '0' && current <= '9') ||
                 (current >= 'a' && current <= 'f'))) {
      return false;
    }
  }
  return true;
}

bool NormalizeConversationUuid(const std::string& value,
                               std::string& normalized) {
  if (value.size() != 36) {
    normalized.clear();
    return false;
  }
  std::string candidate = value;
  normalized.clear();
  bool hasNonZeroDigit = false;
  for (size_t index = 0; index < candidate.size(); ++index) {
    char& current = candidate[index];
    if (index == 8 || index == 13 || index == 18 || index == 23) {
      if (current != '-') {
        return false;
      }
      continue;
    }
    if (current >= 'A' && current <= 'F') {
      current = static_cast<char>(current - 'A' + 'a');
    }
    if (!((current >= '0' && current <= '9') ||
          (current >= 'a' && current <= 'f'))) {
      return false;
    }
    hasNonZeroDigit = hasNonZeroDigit || current != '0';
  }
  if (!hasNonZeroDigit) {
    return false;
  }
  normalized = candidate;
  return true;
}

namespace {

bool IsSafeIdentifier(const std::string& value, size_t maximumLength) {
  if (value.empty() || value.size() > maximumLength) {
    return false;
  }
  for (char current : value) {
    if (!((current >= 'A' && current <= 'Z') ||
          (current >= 'a' && current <= 'z') ||
          (current >= '0' && current <= '9') || current == '-' ||
          current == '_' || current == '.')) {
      return false;
    }
  }
  return true;
}

class Cursor {
 public:
  Cursor(const char* data, size_t length) : data_(data), length_(length) {}

  bool Consume(char expected) {
    SkipWhitespace();
    if (position_ == length_ || data_[position_] != expected) {
      return false;
    }
    ++position_;
    return true;
  }

  bool NextIs(char expected) {
    SkipWhitespace();
    return position_ < length_ && data_[position_] == expected;
  }

  bool AtEnd() {
    SkipWhitespace();
    return position_ == length_;
  }

  bool ReadString(std::string& value, size_t maximumLength) {
    SkipWhitespace();
    if (position_ == length_ || data_[position_++] != '"') {
      return false;
    }
    value.clear();
    while (position_ < length_) {
      const unsigned char current =
          static_cast<unsigned char>(data_[position_++]);
      if (current == '"') {
        return !value.empty();
      }
      if (current < 0x20 || current > 0x7e || current == '\\' ||
          value.size() >= maximumLength) {
        return false;
      }
      value.push_back(static_cast<char>(current));
    }
    return false;
  }

  bool ReadUnsigned(uint32_t& value) {
    SkipWhitespace();
    if (position_ == length_ || data_[position_] < '0' ||
        data_[position_] > '9') {
      return false;
    }
    if (data_[position_] == '0' && position_ + 1 < length_ &&
        data_[position_ + 1] >= '0' && data_[position_ + 1] <= '9') {
      return false;
    }
    uint32_t parsed = 0;
    while (position_ < length_ && data_[position_] >= '0' &&
           data_[position_] <= '9') {
      const uint32_t digit = static_cast<uint32_t>(data_[position_++] - '0');
      if (parsed > (std::numeric_limits<uint32_t>::max() - digit) / 10) {
        return false;
      }
      parsed = parsed * 10 + digit;
    }
    value = parsed;
    return true;
  }

 private:
  void SkipWhitespace() {
    while (position_ < length_ &&
           (data_[position_] == ' ' || data_[position_] == '\r' ||
            data_[position_] == '\n' || data_[position_] == '\t')) {
      ++position_;
    }
  }

  const char* data_;
  size_t length_;
  size_t position_ = 0;
};

bool ParseLimits(Cursor& cursor) {
  if (!cursor.Consume('{')) {
    return false;
  }
  uint8_t seen = 0;
  do {
    std::string key;
    uint32_t value = 0;
    if (!cursor.ReadString(key, 32) || !cursor.Consume(':') ||
        !cursor.ReadUnsigned(value)) {
      return false;
    }
    uint8_t field = 0;
    if (key == "maxTurns" && value == 10) {
      field = 1;
    } else if (key == "maxTextCharacters" && value == 1000) {
      field = 2;
    } else if (key == "idleSeconds" && value == 900) {
      field = 4;
    } else if (key == "maxLifetimeSeconds" && value == 1800) {
      field = 8;
    } else {
      return false;
    }
    if ((seen & field) != 0) {
      return false;
    }
    seen |= field;
  } while (cursor.Consume(','));
  return cursor.Consume('}') && seen == 15;
}

}  // namespace

bool BuildConversationStartRequest(const ConversationStartRequest& request,
                                   std::string& body) {
  body.clear();
  if (!IsSafeIdentifier(request.deviceId, 48) ||
      !IsSafeIdentifier(request.catalogVersion, 128) ||
      !IsConversationUuid(request.bootId) ||
      !IsConversationUuid(request.idempotencyKey) ||
      (!request.testContext.empty() && (!ValidVoiceTestContext(request.testContext) ||
        !request.audioConsent || request.imageConsent))) {
    return false;
  }
  body = "{\"deviceId\":\"" + request.deviceId +
         "\",\"bootId\":\"" + request.bootId +
         "\",\"generation\":" + std::to_string(request.generation) +
         ",\"catalogVersion\":\"" + request.catalogVersion + "\"";
  if (request.operationEpoch != 0) {
    body += ",\"operationEpoch\":\"" + std::to_string(request.operationEpoch) + "\"";
  }
  if (!request.testContext.empty()) body += ",\"testContext\":\"" + request.testContext + "\"";
  body += std::string(",\"locale\":\"cs-CZ\",\"consent\":{\"audio\":") +
         (request.audioConsent ? "true" : "false") + ",\"image\":" +
         (request.imageConsent ? "true" : "false") + "}}";
  return body.size() <= 4096;
}

bool ParseConversationStartResponse(const char* data, size_t length,
                                    ConversationStartResponse& response) {
  response = {};
  if (data == nullptr || length == 0 || length > 512) {
    return false;
  }
  Cursor cursor(data, length);
  ConversationStartResponse parsed;
  if (!cursor.Consume('{')) {
    return false;
  }
  uint8_t seen = 0;
  do {
    std::string key;
    if (!cursor.ReadString(key, 32) || !cursor.Consume(':')) {
      return false;
    }
    uint8_t field = 0;
    if (key == "conversationId") {
      field = 1;
      if (!cursor.ReadString(parsed.conversationId, 64) ||
          !IsConversationUuid(parsed.conversationId)) {
        return false;
      }
    } else if (key == "expiresAt") {
      field = 2;
      if (!cursor.ReadString(parsed.expiresAt, 64)) {
        return false;
      }
    } else if (key == "limits") {
      field = 4;
      if (!ParseLimits(cursor)) {
        return false;
      }
    } else {
      return false;
    }
    if ((seen & field) != 0) {
      return false;
    }
    seen |= field;
  } while (cursor.Consume(','));
  if (!cursor.Consume('}') || !cursor.AtEnd() || seen != 7) {
    return false;
  }
  response = parsed;
  return true;
}

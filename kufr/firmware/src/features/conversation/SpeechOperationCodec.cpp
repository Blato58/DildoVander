#include "SpeechOperationCodec.h"
#include "../../protocol/JsonCursor.h"
#include "ConversationStartCodec.h"

namespace {
using Cursor = kufr_protocol::JsonCursor;

bool ReadStatus(Cursor& cursor, TurnOperationStatus& status) {
  std::string value;
  if (!cursor.ReadString(value, 16, true)) {
    return false;
  }
  if (value == "pending") {
    status = TurnOperationStatus::Pending;
  } else if (value == "running") {
    status = TurnOperationStatus::Running;
  } else if (value == "completed") {
    status = TurnOperationStatus::Completed;
  } else if (value == "failed") {
    status = TurnOperationStatus::Failed;
  } else if (value == "cancelled") {
    status = TurnOperationStatus::Cancelled;
  } else if (value == "unknown") {
    status = TurnOperationStatus::Unknown;
  } else {
    return false;
  }
  return true;
}

bool ReadCorrelation(Cursor& cursor, TurnCorrelation& correlation) {
  if (!cursor.Consume('{')) {
    return false;
  }
  uint8_t seen = 0;
  do {
    std::string key;
    if (!cursor.ReadString(key, 32, true) || !cursor.Consume(':')) {
      return false;
    }
    uint8_t field = 0;
    if (key == "deviceId") {
      field = 1;
      if (!cursor.ReadString(correlation.deviceId, 48, true)) {
        return false;
      }
    } else if (key == "bootId") {
      field = 2;
      if (!cursor.ReadString(correlation.bootId, 36, true) ||
          !IsConversationUuid(correlation.bootId)) {
        return false;
      }
    } else if (key == "conversationId") {
      field = 4;
      if (!cursor.ReadString(correlation.conversationId, 36, true) ||
          !IsConversationUuid(correlation.conversationId)) {
        return false;
      }
    } else if (key == "clientTurnId") {
      field = 8;
      if (!cursor.ReadString(correlation.clientTurnId, 36, true) ||
          !IsConversationUuid(correlation.clientTurnId)) {
        return false;
      }
    } else if (key == "generation") {
      field = 16;
      if (!cursor.ReadUnsigned(correlation.generation)) {
        return false;
      }
    } else if (key == "deviceRevision") {
      field = 32;
      if (!cursor.ReadUnsigned(correlation.deviceRevision)) {
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
  return cursor.Consume('}') && seen == 63;
}

bool ReadSpeechResult(Cursor& cursor, SpeechOperationObservation& parsed, bool& isNull) {
  if (cursor.NextIs('n')) {
    isNull = true;
    return cursor.ReadNull();
  }
  isNull = false;
  if (!cursor.Consume('{')) { return false; }
  uint8_t seen = 0;
  do {
    std::string key;
    if (!cursor.ReadString(key, 32, true) || !cursor.Consume(':')) { return false; }
    uint8_t field = 0;
    if (key == "audioId") {
      field = 1;
      if (!cursor.ReadString(parsed.audioId, 36, true) ||
          !IsConversationUuid(parsed.audioId)) { return false; }
    } else if (key == "expiresAt") {
      field = 2;
      uint64_t seconds = 0;
      if (!cursor.ReadString(parsed.expiresAt, 35, true) ||
          !ParseConversationUtcExpiry(parsed.expiresAt, seconds)) { return false; }
    } else if (key == "outputTarget") {
      field = 4;
      if (!cursor.ReadString(parsed.outputTarget, 16, true) ||
          parsed.outputTarget != "device") { return false; }
    } else { return false; }
    if ((seen & field) != 0) { return false; }
    seen |= field;
  } while (cursor.Consume(','));
  return cursor.Consume('}') && seen == 7;
}
bool ReadError(Cursor& cursor, bool& isNull) {
  if (cursor.NextIs('n')) {
    isNull = true;
    return cursor.ReadNull();
  }
  isNull = false;
  if (!cursor.Consume('{')) {
    return false;
  }
  std::string key;
  std::string code;
  return cursor.ReadString(key, 16, true) && key == "code" &&
         cursor.Consume(':') && cursor.ReadString(code, 64, true) &&
         cursor.Consume('}');
}

}  // namespace

bool ParseSpeechOperation(const char* data, size_t length,
                        SpeechOperationObservation& operation) {
  operation = {};
  if (data == nullptr || length == 0 || length > 2048) {
    return false;
  }
  Cursor cursor(data, length);
  SpeechOperationObservation parsed;
  if (!cursor.Consume('{')) {
    return false;
  }
  uint8_t seen = 0;
  bool resultNull = true;
  bool errorNull = true;
  do {
    std::string key;
    if (!cursor.ReadString(key, 32, true) || !cursor.Consume(':')) {
      return false;
    }
    uint8_t field = 0;
    if (key == "operationId") {
      field = 1;
      if (!cursor.ReadString(parsed.operationId, 36, true) ||
          !IsConversationUuid(parsed.operationId)) {
        return false;
      }
    } else if (key == "kind") {
      field = 2;
      std::string kind;
      if (!cursor.ReadString(kind, 16, true) || kind != "speech") {
        return false;
      }
    } else if (key == "status") {
      field = 4;
      if (!ReadStatus(cursor, parsed.status)) {
        return false;
      }
    } else if (key == "correlation") {
      field = 8;
      if (!ReadCorrelation(cursor, parsed.correlation)) {
        return false;
      }
    } else if (key == "result") {
      field = 16;
      if (!ReadSpeechResult(cursor, parsed, resultNull)) {
        return false;
      }
    } else if (key == "error") {
      field = 32;
      if (!ReadError(cursor, errorNull)) {
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
  if (!cursor.Consume('}') || !cursor.AtEnd() || seen != 63) {
    return false;
  }
  if (parsed.status == TurnOperationStatus::Completed) {
    if (resultNull || !errorNull) {
      return false;
    }
  } else if (!resultNull ||
             (parsed.status == TurnOperationStatus::Failed && errorNull)) {
    return false;
  }
  operation = parsed;
  return true;
}


bool BuildSpeechRequest(const std::string& turnId, std::string& body) {
  body.clear();
  std::string normalized;
  if (!NormalizeConversationUuid(turnId, normalized)) { return false; }
  body = "{\"turnId\":\"" + normalized + "\",\"outputTarget\":\"device\"}";
  return true;
}

uint64_t SpeechRemainingMs(const std::string& operationId,
                           const TurnCorrelation& expected,
                           uint64_t nowUtcSeconds,
                           const SpeechOperationObservation& observed) {
  const TurnCorrelation& actual = observed.correlation;
  uint64_t expiry = 0;
  if (nowUtcSeconds == 0 || !IsConversationUuid(operationId) ||
      observed.operationId != operationId ||
      observed.status != TurnOperationStatus::Completed ||
      actual.deviceId != expected.deviceId || actual.bootId != expected.bootId ||
      actual.conversationId != expected.conversationId ||
      actual.clientTurnId != expected.clientTurnId ||
      actual.generation != expected.generation ||
      actual.deviceRevision != expected.deviceRevision ||
      !IsConversationUuid(observed.audioId) || observed.outputTarget != "device" ||
      !ParseConversationUtcExpiry(observed.expiresAt, expiry) ||
      expiry <= nowUtcSeconds || expiry - nowUtcSeconds <= 1 ||
      expiry - nowUtcSeconds > 120) {
    return 0;
  }
  return (expiry - nowUtcSeconds - 1) * 1000;
}


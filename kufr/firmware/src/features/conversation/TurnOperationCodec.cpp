#include "TurnOperationCodec.h"

#include <limits>

#include "ConversationSession.h"
#include "ConversationStartCodec.h"

namespace {

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

  bool ReadNull() {
    SkipWhitespace();
    if (length_ - position_ < 4 || data_[position_] != 'n' ||
        data_[position_ + 1] != 'u' || data_[position_ + 2] != 'l' ||
        data_[position_ + 3] != 'l') {
      return false;
    }
    position_ += 4;
    return true;
  }

  bool ReadUnsigned(uint64_t& value) {
    SkipWhitespace();
    if (position_ == length_ || data_[position_] < '0' ||
        data_[position_] > '9') {
      return false;
    }
    if (data_[position_] == '0' && position_ + 1 < length_ &&
        data_[position_ + 1] >= '0' && data_[position_ + 1] <= '9') {
      return false;
    }
    uint64_t parsed = 0;
    while (position_ < length_ && data_[position_] >= '0' &&
           data_[position_] <= '9') {
      const uint64_t digit = static_cast<uint64_t>(data_[position_++] - '0');
      if (parsed > (std::numeric_limits<uint64_t>::max() - digit) / 10) {
        return false;
      }
      parsed = parsed * 10 + digit;
    }
    value = parsed;
    return true;
  }

  bool ReadString(std::string& value, size_t maximumBytes,
                  bool asciiOnly = false) {
    SkipWhitespace();
    if (position_ == length_ || data_[position_++] != '"') {
      return false;
    }
    value.clear();
    while (position_ < length_) {
      const unsigned char current =
          static_cast<unsigned char>(data_[position_++]);
      if (current == '"') {
        return value.size() <= maximumBytes;
      }
      if (current < 0x20) {
        return false;
      }
      if (current != '\\') {
        if (asciiOnly && current > 0x7e) {
          return false;
        }
        value.push_back(static_cast<char>(current));
      } else {
        if (asciiOnly || position_ == length_) {
          return false;
        }
        const char escape = data_[position_++];
        if (escape == 'u') {
          uint32_t codePoint = 0;
          if (!ReadHex4(codePoint)) {
            return false;
          }
          if (codePoint >= 0xd800 && codePoint <= 0xdbff) {
            if (length_ - position_ < 2 || data_[position_] != '\\' ||
                data_[position_ + 1] != 'u') {
              return false;
            }
            position_ += 2;
            uint32_t low = 0;
            if (!ReadHex4(low) || low < 0xdc00 || low > 0xdfff) {
              return false;
            }
            codePoint = 0x10000 + ((codePoint - 0xd800) << 10) +
                        (low - 0xdc00);
          } else if (codePoint >= 0xdc00 && codePoint <= 0xdfff) {
            return false;
          }
          AppendUtf8(codePoint, value);
        } else if (escape == '"' || escape == '\\' || escape == '/') {
          value.push_back(escape);
        } else if (escape == 'b' || escape == 'f' || escape == 'n' ||
                   escape == 'r' || escape == 't') {
          const char decoded = escape == 'b' ? '\b' :
                               escape == 'f' ? '\f' :
                               escape == 'n' ? '\n' :
                               escape == 'r' ? '\r' : '\t';
          value.push_back(decoded);
        } else {
          return false;
        }
      }
      if (value.size() > maximumBytes) {
        return false;
      }
    }
    return false;
  }

 private:
  void SkipWhitespace() {
    while (position_ < length_ &&
           (data_[position_] == ' ' || data_[position_] == '\r' ||
            data_[position_] == '\n' || data_[position_] == '\t')) {
      ++position_;
    }
  }

  bool ReadHex4(uint32_t& value) {
    if (length_ - position_ < 4) {
      return false;
    }
    value = 0;
    for (size_t index = 0; index < 4; ++index) {
      const char digit = data_[position_++];
      value <<= 4;
      if (digit >= '0' && digit <= '9') {
        value += static_cast<uint32_t>(digit - '0');
      } else if (digit >= 'a' && digit <= 'f') {
        value += static_cast<uint32_t>(digit - 'a' + 10);
      } else if (digit >= 'A' && digit <= 'F') {
        value += static_cast<uint32_t>(digit - 'A' + 10);
      } else {
        return false;
      }
    }
    return true;
  }

  static void AppendUtf8(uint32_t codePoint, std::string& value) {
    if (codePoint <= 0x7f) {
      value.push_back(static_cast<char>(codePoint));
    } else if (codePoint <= 0x7ff) {
      value.push_back(static_cast<char>(0xc0 | (codePoint >> 6)));
      value.push_back(static_cast<char>(0x80 | (codePoint & 0x3f)));
    } else if (codePoint <= 0xffff) {
      value.push_back(static_cast<char>(0xe0 | (codePoint >> 12)));
      value.push_back(static_cast<char>(0x80 | ((codePoint >> 6) & 0x3f)));
      value.push_back(static_cast<char>(0x80 | (codePoint & 0x3f)));
    } else {
      value.push_back(static_cast<char>(0xf0 | (codePoint >> 18)));
      value.push_back(static_cast<char>(0x80 | ((codePoint >> 12) & 0x3f)));
      value.push_back(static_cast<char>(0x80 | ((codePoint >> 6) & 0x3f)));
      value.push_back(static_cast<char>(0x80 | (codePoint & 0x3f)));
    }
  }

  const char* data_;
  size_t length_;
  size_t position_ = 0;
};

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

bool ReadProposal(Cursor& cursor, TurnProposal& proposal) {
  if (!cursor.Consume('{')) {
    return false;
  }
  uint16_t seen = 0;
  do {
    std::string key;
    if (!cursor.ReadString(key, 32, true) || !cursor.Consume(':')) {
      return false;
    }
    uint16_t field = 0;
    if (key == "proposalId") {
      field = 1;
      if (!cursor.ReadString(proposal.proposalId, 36, true) ||
          !IsConversationUuid(proposal.proposalId)) return false;
    } else if (key == "conversationId") {
      field = 2;
      if (!cursor.ReadString(proposal.conversationId, 36, true) ||
          !IsConversationUuid(proposal.conversationId)) return false;
    } else if (key == "turnId") {
      field = 4;
      if (!cursor.ReadString(proposal.turnId, 36, true) ||
          !IsConversationUuid(proposal.turnId)) return false;
    } else if (key == "catalogVersion") {
      field = 8;
      if (!cursor.ReadString(proposal.catalogVersion, 128, true) ||
          proposal.catalogVersion.empty()) return false;
    } else if (key == "deviceRevision") {
      field = 16;
      if (!cursor.ReadUnsigned(proposal.deviceRevision)) return false;
    } else if (key == "compartment") {
      field = 32;
      uint64_t compartment = 0;
      if (!cursor.ReadUnsigned(compartment) || compartment < 1 ||
          compartment > 6) return false;
      proposal.compartment = static_cast<uint8_t>(compartment);
    } else if (key == "reason") {
      field = 64;
      if (!cursor.ReadString(proposal.reason, 800) ||
          !IsValidConversationUtf8(proposal.reason, 200, true)) return false;
    } else if (key == "bootId") {
      field = 128;
      if (!cursor.ReadString(proposal.bootId, 36, true) ||
          !IsConversationUuid(proposal.bootId)) return false;
    } else if (key == "generation") {
      field = 256;
      if (!cursor.ReadUnsigned(proposal.generation)) return false;
    } else if (key == "expiresAt") {
      field = 512;
      if (!cursor.ReadString(proposal.expiresAt, 35, true)) return false;
    } else {
      return false;
    }
    if ((seen & field) != 0) return false;
    seen |= field;
  } while (cursor.Consume(','));
  return cursor.Consume('}') && seen == 1023;
}

bool ReadResult(Cursor& cursor, TurnOperationObservation& operation,
                bool& isNull) {
  if (cursor.NextIs('n')) {
    isNull = true;
    return cursor.ReadNull();
  }
  isNull = false;
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
    if (key == "reply") {
      field = 1;
      if (!cursor.ReadString(operation.reply, 2000) ||
          !IsValidConversationUtf8(operation.reply, 500, false)) {
        return false;
      }
    } else if (key == "decision") {
      field = 2;
      if (!cursor.ReadString(operation.decision, 16, true)) {
        return false;
      }
    } else if (key == "reason") {
      field = 4;
      std::string reason;
      if (!cursor.ReadString(reason, 800) ||
          !IsValidConversationUtf8(reason, 200, true)) {
        return false;
      }
    } else if (key == "proposal") {
      field = 8;
      if (cursor.NextIs('n')) {
        if (!cursor.ReadNull()) return false;
      } else {
        if (!ReadProposal(cursor, operation.proposal)) return false;
        operation.hasProposal = true;
      }
    } else {
      return false;
    }
    if ((seen & field) != 0) {
      return false;
    }
    seen |= field;
  } while (cursor.Consume(','));
  return cursor.Consume('}') && seen == 15 &&
         ((operation.decision == "propose" && operation.hasProposal) ||
          ((operation.decision == "ask" ||
            operation.decision == "no_selection") && !operation.hasProposal));
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

bool ParseTurnOperation(const char* data, size_t length,
                        TurnOperationObservation& operation) {
  operation = {};
  if (data == nullptr || length == 0 || length > 4096) {
    return false;
  }
  Cursor cursor(data, length);
  TurnOperationObservation parsed;
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
      if (!cursor.ReadString(parsed.kind, 16, true) ||
          parsed.kind != "turn") {
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
      if (!ReadResult(cursor, parsed, resultNull)) {
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

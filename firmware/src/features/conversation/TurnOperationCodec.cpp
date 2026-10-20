#include "TurnOperationCodec.h"

#include "../../protocol/JsonCursor.h"

#include "ConversationSession.h"
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

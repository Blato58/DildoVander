#include "LocalControlCodec.h"

#include "../conversation/ConversationSession.h"
#include "../conversation/ConversationStartCodec.h"
#include "../../protocol/JsonCursor.h"

namespace {

using kufr_protocol::JsonCursor;

constexpr uint64_t MaximumSafeInteger = 9007199254740991ULL;

enum Field : uint32_t {
  Version = 1U << 0,
  Type = 1U << 1,
  MessageId = 1U << 2,
  DeviceId = 1U << 3,
  BootId = 1U << 4,
  LeaseId = 1U << 5,
  ConversationId = 1U << 6,
  ClientTurnId = 1U << 7,
  Generation = 1U << 8,
  Revision = 1U << 9,
  Text = 1U << 10,
  CommandId = 1U << 11,
  Sequence = 1U << 12,
  IssuedAt = 1U << 13,
  MaxAge = 1U << 14,
  Payload = 1U << 15,
  Inspection = 1U << 16,
  VoiceMode = 1U << 17,
  Voice = 1U << 18,
};

bool ReadSafeInteger(JsonCursor& cursor, uint64_t& value) {
  return cursor.ReadUnsigned(value) && value <= MaximumSafeInteger;
}

bool ReadUuid(JsonCursor& cursor, std::string& value) {
  std::string parsed;
  return cursor.ReadString(parsed, 36) &&
         NormalizeConversationUuid(parsed, value);
}

bool IsDeviceId(const std::string& value) {
  if (value.empty() || value.size() > 64 ||
      value.front() < 'a' || value.front() > 'z') {
    return false;
  }
  for (char current : value) {
    if (!((current >= 'a' && current <= 'z') ||
          (current >= '0' && current <= '9') || current == '-')) {
      return false;
    }
  }
  return true;
}

bool ReadKind(JsonCursor& cursor, LocalControlMessageKind& kind) {
  std::string type;
  if (!cursor.ReadString(type, 32)) {
    return false;
  }
  if (type == "hello") {
    kind = LocalControlMessageKind::Hello;
  } else if (type == "state.get") {
    kind = LocalControlMessageKind::StateGet;
  } else if (type == "control.acquire") {
    kind = LocalControlMessageKind::Acquire;
  } else if (type == "control.release") {
    kind = LocalControlMessageKind::Release;
  } else if (type == "heartbeat") {
    kind = LocalControlMessageKind::Heartbeat;
  } else if (type == "conversation.text") {
    kind = LocalControlMessageKind::Text;
  } else if (type == "conversation.cancel") {
    kind = LocalControlMessageKind::Cancel;
  } else if (type == "voice.configure") {
    kind = LocalControlMessageKind::VoiceConfigure;
  } else if (type == "voice.start") {
    kind = LocalControlMessageKind::VoiceStart;
  } else if (type == "voice.stop") {
    kind = LocalControlMessageKind::VoiceStop;
  } else if (type == "safety.stop") {
    kind = LocalControlMessageKind::Stop;
  } else if (type == "safety.reset") {
    kind = LocalControlMessageKind::Reset;
  } else if (type == "selection.start") {
    kind = LocalControlMessageKind::Selection;
  } else if (type == "inventory.confirmReload") {
    kind = LocalControlMessageKind::Reload;
  } else {
    return false;
  }
  return true;
}

bool ReadSource(JsonCursor& cursor, SelectionSource& source) {
  std::string value;
  if (!cursor.ReadString(value, 32)) {
    return false;
  }
  if (value == "manual") {
    source = SelectionSource::Manual;
  } else if (value == "operatorConfirmedAi") {
    source = SelectionSource::OperatorConfirmedAi;
  } else if (value == "operatorCorrectedAi") {
    source = SelectionSource::OperatorCorrectedAi;
  } else if (value == "buttonConfirmedAi") {
    source = SelectionSource::ButtonConfirmedAi;
  } else {
    return false;
  }
  return true;
}

bool ReadPayload(JsonCursor& cursor, LocalControlMessage& message,
                 uint8_t& seen) {
  if (!cursor.Consume('{')) {
    return false;
  }
  seen = 0;
  do {
    std::string key;
    if (!cursor.ReadString(key, 32) || !cursor.Consume(':')) {
      return false;
    }
    uint8_t field = 0;
    if (key == "compartment") {
      field = 1;
      uint64_t value = 0;
      if (!cursor.ReadUnsigned(value) || value < 1 || value > 6) {
        return false;
      }
      message.compartment = static_cast<uint8_t>(value);
    } else if (key == "selectionSource") {
      field = 2;
      if (!ReadSource(cursor, message.source)) {
        return false;
      }
    } else if (key == "inspectionConfirmed") {
      field = 4;
      if (!cursor.ReadTrue()) {
        return false;
      }
    } else {
      return false;
    }
    if ((seen & field) != 0) {
      return false;
    }
    seen = static_cast<uint8_t>(seen | field);
  } while (cursor.Consume(','));
  return cursor.Consume('}');
}

uint32_t ExpectedFields(LocalControlMessageKind kind) {
  constexpr uint32_t HelloFields = Version | Type | MessageId | DeviceId;
  constexpr uint32_t Common = HelloFields | BootId;
  constexpr uint32_t Manual = Common | LeaseId;
  constexpr uint32_t Physical = Version | Type | DeviceId | BootId | LeaseId |
                                CommandId | Sequence | Revision | IssuedAt |
                                MaxAge | Payload;
  switch (kind) {
    case LocalControlMessageKind::Hello:
      return HelloFields;
    case LocalControlMessageKind::StateGet:
    case LocalControlMessageKind::Acquire:
    case LocalControlMessageKind::Stop:
      return Common;
    case LocalControlMessageKind::Release:
    case LocalControlMessageKind::Heartbeat:
      return Manual;
    case LocalControlMessageKind::Text:
      return Manual | ConversationId | ClientTurnId | Generation | Revision | Text;
    case LocalControlMessageKind::Cancel:
      return Manual | ConversationId | Generation;
    case LocalControlMessageKind::Reset:
      return Manual | Inspection;
    case LocalControlMessageKind::VoiceConfigure:
      return Common | VoiceMode | Voice;
    case LocalControlMessageKind::VoiceStart:
      return Common | Revision;
    case LocalControlMessageKind::VoiceStop:
      return Common;
    case LocalControlMessageKind::Selection:
    case LocalControlMessageKind::Reload:
      return Physical;
  }
  return 0;
}

bool Parse(JsonCursor& cursor, LocalControlMessage& message, size_t length) {
  if (!cursor.Consume('{')) {
    return false;
  }
  uint32_t seen = 0;
  uint8_t payloadFields = 0;
  do {
    std::string key;
    if (!cursor.ReadString(key, 32) || !cursor.Consume(':')) {
      return false;
    }
    uint32_t field = 0;
    bool valid = false;
    if (key == "protocolVersion") {
      field = Version;
      uint64_t version = 0;
      valid = cursor.ReadUnsigned(version) && version == 1;
    } else if (key == "type") {
      field = Type;
      valid = ReadKind(cursor, message.kind);
    } else if (key == "messageId") {
      field = MessageId;
      valid = ReadUuid(cursor, message.messageId);
    } else if (key == "deviceId") {
      field = DeviceId;
      valid = cursor.ReadString(message.deviceId, 64) && IsDeviceId(message.deviceId);
    } else if (key == "bootId") {
      field = BootId;
      valid = ReadUuid(cursor, message.bootId);
    } else if (key == "controlLeaseId") {
      field = LeaseId;
      valid = ReadUuid(cursor, message.controlLeaseId);
    } else if (key == "conversationId") {
      field = ConversationId;
      valid = ReadUuid(cursor, message.conversationId);
    } else if (key == "clientTurnId") {
      field = ClientTurnId;
      valid = ReadUuid(cursor, message.clientTurnId);
    } else if (key == "generation") {
      field = Generation;
      valid = ReadSafeInteger(cursor, message.generation);
    } else if (key == "expectedRevision") {
      field = Revision;
      valid = ReadSafeInteger(cursor, message.expectedRevision);
    } else if (key == "text") {
      field = Text;
      valid = cursor.ReadString(message.text, 4000) && IsValidConversationText(message.text);
    } else if (key == "commandId") {
      field = CommandId;
      valid = ReadUuid(cursor, message.commandId);
    } else if (key == "sequence") {
      field = Sequence;
      valid = ReadSafeInteger(cursor, message.sequence);
    } else if (key == "issuedAtDeviceMs") {
      field = IssuedAt;
      valid = ReadSafeInteger(cursor, message.issuedAtDeviceMs);
    } else if (key == "maxAgeMs") {
      field = MaxAge;
      uint64_t value = 0;
      valid = cursor.ReadUnsigned(value) && value >= 1 && value <= 2000;
      message.maxAgeMs = static_cast<uint32_t>(value);
    } else if (key == "payload") {
      field = Payload;
      valid = ReadPayload(cursor, message, payloadFields);
    } else if (key == "mode") {
      field = VoiceMode;
      valid = cursor.ReadString(message.voiceMode, 16, true) && (message.voiceMode == "off" || message.voiceMode == "realtime" || message.voiceMode == "gpt-live");
    } else if (key == "voice") {
      field = Voice;
      valid = cursor.ReadString(message.voice, 16, true) && (message.voice == "cedar" || message.voice == "marin" || message.voice == "coral");
    } else if (key == "inspectionConfirmed") {
      field = Inspection;
      valid = cursor.ReadTrue();
    }
    if (!valid || (seen & field) != 0) {
      return false;
    }
    seen |= field;
  } while (cursor.Consume(','));

  if (!cursor.Consume('}') || !cursor.AtEnd() ||
      seen != ExpectedFields(message.kind) ||
      (message.kind != LocalControlMessageKind::Text && length > 2048)) {
    return false;
  }
  switch (message.kind) {
    case LocalControlMessageKind::Selection:
      return payloadFields == 3;
    case LocalControlMessageKind::Reload:
      return payloadFields == 5;
    default:
      return payloadFields == 0;
  }
}

}  // namespace

bool ParseLocalControlMessage(const char* data, size_t length,
                              LocalControlMessage& message) {
  message = {};
  if (data == nullptr || length == 0 || length > 8192 ||
      !IsValidConversationUtf8(std::string(data, length), 8192, false)) {
    return false;
  }
  JsonCursor cursor(data, length);
  LocalControlMessage parsed;
  if (!Parse(cursor, parsed, length)) {
    return false;
  }
  message = std::move(parsed);
  return true;
}

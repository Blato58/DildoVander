#include "DeviceMediaCodec.h"
#include "../../protocol/JsonCursor.h"
#include "../conversation/ConversationStartCodec.h"
#include <cstring>
#include <limits>

namespace kufr_media {
namespace {
uint32_t Read32(const uint8_t* bytes) {
  return static_cast<uint32_t>(bytes[0]) | (static_cast<uint32_t>(bytes[1]) << 8) |
      (static_cast<uint32_t>(bytes[2]) << 16) | (static_cast<uint32_t>(bytes[3]) << 24);
}
void Write32(uint8_t* bytes, uint32_t value) {
  for (unsigned i = 0; i != 4; ++i) bytes[i] = static_cast<uint8_t>(value >> (i * 8));
}
bool ReadUuid(kufr_protocol::JsonCursor& cursor, std::string& value) {
  std::string normalized;
  return cursor.ReadString(value, 36, true) && NormalizeConversationUuid(value, normalized) && normalized == value;
}
}
bool ReadOutputPcm(const uint8_t* message, size_t length, PcmFrame& frame) {
  frame = {};
  if (!message || length <= HeaderBytes || length > HeaderBytes + MaximumPcmBytes ||
      (length - HeaderBytes) % 2 || message[0] != 'K' || message[1] != 'F' ||
      message[2] != 1 || message[3] != 2 || Read32(message + 12) != length - HeaderBytes) return false;
  const uint32_t stream = Read32(message + 4), sequence = Read32(message + 8);
  if (!stream || sequence == std::numeric_limits<uint32_t>::max()) return false;
  frame = {stream, sequence, message + HeaderBytes, length - HeaderBytes};
  return true;
}
bool WriteInputPcm(uint32_t stream, uint32_t sequence, const uint8_t* pcm,
    size_t length, uint8_t* message, size_t capacity, size_t& used) {
  used = 0;
  if (!stream || sequence == std::numeric_limits<uint32_t>::max() || !pcm || !length ||
      length > MaximumPcmBytes || length % 2 || !message || capacity < HeaderBytes + length) return false;
  message[0] = 'K'; message[1] = 'F'; message[2] = 1; message[3] = 1;
  Write32(message + 4, stream); Write32(message + 8, sequence); Write32(message + 12, static_cast<uint32_t>(length));
  std::memcpy(message + HeaderBytes, pcm, length);
  used = HeaderBytes + length;
  return true;
}
bool ReadControl(const char* message, size_t length, Control& control) {
  control = {};
  if (!message || !length || length > 1024) return false;
  kufr_protocol::JsonCursor cursor(message, length);
  if (!cursor.Consume('{')) return false;
  uint32_t seen = 0;
  std::string type, format;
  uint64_t stream = 0, sequence = 0, status = 0, maximum = 0, credit = 0;
  do {
    std::string key;
    if (!cursor.ReadString(key, 32, true) || !cursor.Consume(':')) return false;
    uint32_t field = 0;
    bool valid = false;
    if (key == "type") { field = 1; valid = cursor.ReadString(type, 32, true); }
    else if (key == "stream") { field = 2; valid = cursor.ReadUnsigned(stream) && stream > 0 && stream <= UINT32_MAX; }
    else if (key == "sequence") { field = 4; valid = cursor.ReadUnsigned(sequence) && sequence < UINT32_MAX; }
    else if (key == "inputId") { field = 8; valid = ReadUuid(cursor, control.inputId); }
    else if (key == "responseGeneration") { field = 16; valid = cursor.ReadUnsigned(control.generation) && control.generation <= 9007199254740991ULL; }
    else if (key == "format") { field = 32; valid = cursor.ReadString(format, 32, true) && format == "pcm16-mono-24000"; }
    else if (key == "maxFrameBytes") { field = 64; valid = cursor.ReadUnsigned(maximum) && maximum == MaximumPcmBytes; }
    else if (key == "credit") { field = 128; valid = cursor.ReadUnsigned(credit) && credit == 1; }
    else if (key == "operationId") { field = 256; valid = ReadUuid(cursor, control.operationId); }
    else if (key == "requestId") { field = 512; valid = ReadUuid(cursor, control.requestId); }
    else if (key == "status") { field = 1024; valid = cursor.ReadUnsigned(status) && (status == 204 || status == 409); }
    else if (key == "code") { field = 2048; valid = cursor.ReadString(control.code, 64, true); }
    if (!valid || (seen & field)) return false;
    seen |= field;
  } while (cursor.Consume(','));
  if (!cursor.Consume('}') || !cursor.AtEnd()) return false;
  uint32_t expected = 0;
  if (type == "media.input") { control.kind = ControlKind::Input; expected = 1 | 2 | 8 | 32 | 64 | 128; }
  else if (type == "media.output") { control.kind = ControlKind::Output; expected = 1 | 2 | 16 | 32; }
  else if (type == "media.operation") { control.kind = ControlKind::Operation; expected = 1 | 16 | 256; }
  else if (type == "media.ack") { control.kind = ControlKind::InputAck; expected = 1 | 2 | 4; }
  else if (type == "control.ack") {
    control.kind = ControlKind::ControlAck;
    expected = 1 | 512 | 1024 | (status == 409 ? 2048 : 0);
    if (status == 409 && control.code != "conversation_restart_required" && control.code != "conversation_version_mismatch" &&
        control.code != "current_operation_not_ready" && control.code != "media_cancel_mismatch" &&
        control.code != "media_playback_mismatch") return false;
  }
  if (!expected || seen != expected) return false;
  control.stream = static_cast<uint32_t>(stream); control.sequence = static_cast<uint32_t>(sequence);
  control.status = static_cast<unsigned>(status);
  return true;
}
}

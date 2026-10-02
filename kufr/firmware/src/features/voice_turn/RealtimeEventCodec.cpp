#include "RealtimeEventCodec.h"
#include "../../protocol/JsonCursor.h"
#include "../conversation/ConversationSession.h"
#include "../conversation/ConversationStartCodec.h"

bool ParseRealtimeEvent(const std::string& line, RealtimeEvent& event) {
  event = {};
  if (line.empty() || line.size() >= 65536 || !IsValidConversationUtf8(line, 65535, false)) return false;
  kufr_protocol::JsonCursor cursor(line.data(), line.size());
  if (!cursor.Consume('{')) return false;
  uint32_t seen = 0;
  std::string correlation, result;
  do {
    std::string key, ignored;
    uint64_t cost = 0;
    if (!cursor.ReadString(key, 32, true) || !cursor.Consume(':')) return false;
    uint32_t field = 0;
    bool valid = false;
    if (key == "type") { field = 1; valid = cursor.ReadString(event.type, 32, true); }
    else if (key == "streamId") { field = 2; valid = cursor.ReadString(event.streamId, 36, true) && IsConversationUuid(event.streamId); }
    else if (key == "audioFormat") { field = 4; valid = cursor.ReadString(ignored, 32, true) && ignored == "pcm16-mono-24000"; }
    else if (key == "inputId") { field = 8; valid = cursor.ReadString(event.inputId, 36, true) && IsConversationUuid(event.inputId); }
    else if (key == "operationId") { field = 16; valid = cursor.ReadString(event.operationId, 36, true) && IsConversationUuid(event.operationId); }
    else if (key == "correlation") { field = 32; valid = cursor.ReadObjectJson(correlation, 1024); }
    else if (key == "delta") { field = 64; valid = cursor.ReadString(event.delta, 43692, true) && !event.delta.empty(); }
    else if (key == "text") { field = 128; valid = cursor.ReadString(ignored, 4000) && IsValidConversationUtf8(ignored, 1000, false); }
    else if (key == "result") { field = 256; valid = cursor.ReadObjectJson(result, 3072); }
    else if (key == "costMicroUsd") { field = 512; valid = cursor.ReadUnsigned(cost) && cost <= 9007199254740991ULL; }
    else if (key == "code") { field = 1024; valid = cursor.ReadString(event.code, 64, true); }
    else if (key == "reason") { field = 2048; valid = cursor.ReadString(ignored, 32, true) && ignored == "conversation_terminal"; }
    if (!valid || seen & field) return false;
    seen |= field;
  } while (cursor.Consume(','));
  if (!cursor.Consume('}') || !cursor.AtEnd()) return false;
  uint32_t expected = 0;
  if (event.type == "session") expected = 1 | 2 | 4;
  else if (event.type == "listening" || event.type == "speech_started" || event.type == "speech_stopped") expected = 1 | 8;
  else if (event.type == "started") expected = 1 | 16 | 32;
  else if (event.type == "audio") expected = 1 | 64;
  else if (event.type == "visitor") expected = 1 | 128;
  else if (event.type == "completed") expected = 1 | 16 | 32 | 256 | 512;
  else if (event.type == "failed") expected = 1 | 1024;
  else if (event.type == "closed") expected = 1 | 2048;
  else if ((event.type == "heartbeat" || event.type == "pause")) expected = 1;
  if (expected == 0 || seen != expected) return false;
  if (event.type == "started" || event.type == "completed") {
    if (result.empty()) result = "{\"reply\":\"pending\",\"decision\":\"ask\",\"reason\":\"\",\"proposal\":null}";
    const std::string operation = "{\"operationId\":\"" + event.operationId +
        "\",\"kind\":\"turn\",\"status\":\"completed\",\"correlation\":" +
        correlation + ",\"result\":" + result + ",\"error\":null}";
    if (!ParseTurnOperation(operation.data(), operation.size(), event.operation)) return false;
  }
  return true;
}

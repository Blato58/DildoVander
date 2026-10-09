#include "CostumePhoto.h"
#include <algorithm>
#include "../../protocol/JsonCursor.h"
#include "../conversation/ConversationStartCodec.h"
#include "../conversation/ConversationSession.h"

void WipeCostumeJpeg(std::vector<uint8_t>& jpeg) {
  volatile uint8_t* bytes = jpeg.empty() ? nullptr : jpeg.data();
  for (size_t i = 0; i < jpeg.size(); ++i) bytes[i] = 0;
  jpeg.clear();
}

bool ValidCostumeJpeg(const uint8_t* data, size_t size) {
  if (!data || size < 12 || size > MaximumCostumeJpegBytes ||
      data[0] != 255 || data[1] != 216 || data[size - 2] != 255 || data[size - 1] != 217) return false;
  size_t position = 2;
  bool frame = false, scan = false, quantization = false, huffman = false;
  while (position < size - 2) {
    if (data[position++] != 255) return false;
    while (position < size && data[position] == 255) ++position;
    if (position >= size) return false;
    const uint8_t marker = data[position++];
    if (marker == 0 || marker == 216 || marker == 217 || (marker >= 208 && marker <= 215) || position + 2 > size) return false;
    const size_t length = (static_cast<size_t>(data[position]) << 8) | data[position + 1];
    if (length < 2 || length > size - 2 - position) return false;
    if ((marker >= 225 && marker <= 239) || marker == 254) return false;
    if (marker == 224 && (length != 16 || data[position + 2] != 'J' || data[position + 3] != 'F' ||
        data[position + 4] != 'I' || data[position + 5] != 'F' || data[position + 6] != 0 ||
        data[position + 14] != 0 || data[position + 15] != 0)) return false;
    if (marker == 219) quantization = true;
    if (marker == 196) huffman = true;
    if (marker >= 192 && marker <= 207 && marker != 196 && marker != 200 && marker != 204) {
      if (frame || (marker != 192 && marker != 194) || length < 11 || data[position + 2] != 8) return false;
      const size_t height = (static_cast<size_t>(data[position + 3]) << 8) | data[position + 4];
      const size_t width = (static_cast<size_t>(data[position + 5]) << 8) | data[position + 6];
      const size_t components = data[position + 7];
      if (!width || width > 640 || !height || height > 640 || (components != 1 && components != 3) || length != 8 + components * 3) return false;
      frame = true;
    }
    position += length;
    if (marker == 218) {
      if (!frame || !quantization || !huffman || length < 8) return false;
      const size_t components = data[position - length + 2];
      if (components < 1 || components > 3 || length != 6 + components * 2) return false;
      const size_t entropy = position;
      scan = true;
      while (position < size - 2) {
        if (data[position] != 255) { ++position; continue; }
        if (data[position + 1] == 0 || (data[position + 1] >= 208 && data[position + 1] <= 215)) { position += 2; continue; }
        break;
      }
      if (position == entropy) return false;
    }
  }
  return frame && scan && position == size - 2;
}

PhotoOutcome PrepareCostumePhoto(bool consent, CostumeCameraPort& camera,
    CostumeUploadPort& transport, const std::function<bool()>& current,
    const std::function<uint64_t()>& now, const std::function<void()>& wait,
    const std::function<void(const char*)>& phase) {
  if (!consent) return PhotoOutcome::Skipped;
  if (!current()) return PhotoOutcome::Cancelled;
  if (!camera.Available()) return PhotoOutcome::Unavailable;
  const uint64_t started = now();
  const auto active = [&]() { return current() && now() - started < 120000; };
  std::vector<uint8_t> jpeg;
  phase("photo_capture");
  const bool captured = camera.Capture(jpeg, active);
  if (!active()) { WipeCostumeJpeg(jpeg); return current() ? PhotoOutcome::Failed : PhotoOutcome::Cancelled; }
  if (!captured || !ValidCostumeJpeg(jpeg.data(), jpeg.size())) {
    WipeCostumeJpeg(jpeg); return PhotoOutcome::Failed;
  }
  std::string operationId;
  phase("photo_upload");
  const bool uploaded = transport.Upload(jpeg, operationId);
  WipeCostumeJpeg(jpeg);
  if (!active()) return current() ? PhotoOutcome::Failed : PhotoOutcome::Cancelled;
  if (!uploaded || !IsConversationUuid(operationId)) return PhotoOutcome::Failed;
  phase("photo_wait");
  while (active()) {
    const PhotoPoll result = transport.Poll(operationId);
    if (!active()) return current() ? PhotoOutcome::Failed : PhotoOutcome::Cancelled;
    if (result == PhotoPoll::Completed) return PhotoOutcome::Completed;
    if (result == PhotoPoll::Failed) return PhotoOutcome::Failed;
    wait();
  }
  return current() ? PhotoOutcome::Failed : PhotoOutcome::Cancelled;
}

namespace {
using Cursor = kufr_protocol::JsonCursor;
bool ReadCorrelation(Cursor& cursor, const TurnCorrelation& expected) {
  if (!cursor.Consume('{')) return false;
  uint8_t seen = 0;
  do {
    std::string key, value;
    uint8_t field = 0;
    if (!cursor.ReadString(key, 32, true) || !cursor.Consume(':')) return false;
    if (key == "generation" || key == "deviceRevision") {
      uint64_t number = 0;
      field = key == "generation" ? 16 : 32;
      if (!cursor.ReadUnsigned(number) || number != (field == 16 ? expected.generation : expected.deviceRevision)) return false;
    } else {
      if (!cursor.ReadString(value, 64, true)) return false;
      if (key == "deviceId") { field = 1; if (value != expected.deviceId) return false; }
      else if (key == "bootId") { field = 2; if (value != expected.bootId) return false; }
      else if (key == "conversationId") { field = 4; if (value != expected.conversationId) return false; }
      else if (key == "clientTurnId") { field = 8; if (value != expected.clientTurnId) return false; }
      else return false;
    }
    if (seen & field) return false;
    seen |= field;
  } while (cursor.Consume(','));
  return cursor.Consume('}') && seen == 63;
}
bool ReadResult(Cursor& cursor) {
  if (!cursor.Consume('{')) return false;
  uint8_t seen = 0;
  do {
    std::string key, value;
    uint8_t field = 0;
    if (!cursor.ReadString(key, 32, true) || !cursor.Consume(':') || !cursor.ReadString(value, 960)) return false;
    if (key == "description") { field = 1; if (!IsValidConversationUtf8(value, 240, false)) return false; }
    else if (key == "possibleCharacter") { field = 2; if (!IsValidConversationUtf8(value, 80, true)) return false; }
    else if (key == "uncertainty") { field = 4; if (value != "low" && value != "medium" && value != "high") return false; }
    else if (key == "source") { field = 8; if (value != "photo" && value != "fake") return false; }
    else if (key == "expiresAt") { field = 16; uint64_t expiry = 0; if (!ParseConversationUtcExpiry(value, expiry)) return false; }
    else return false;
    if (seen & field) return false;
    seen |= field;
  } while (cursor.Consume(','));
  return cursor.Consume('}') && seen == 31;
}
}

bool ParseImageAccepted(const std::string& json, std::string& operationId) {
  operationId.clear();
  if (json.size() > 256) return false;
  Cursor cursor(json.data(), json.size());
  uint8_t seen = 0;
  std::string parsed;
  if (!cursor.Consume('{')) return false;
  do {
    std::string key, value;
    uint8_t field = 0;
    if (!cursor.ReadString(key, 32, true) || !cursor.Consume(':') || !cursor.ReadString(value, 36, true)) return false;
    if (key == "operationId" && IsConversationUuid(value)) { field = 1; parsed = value; }
    else if (key == "kind" && value == "image") field = 2;
    else if (key == "status" && value == "pending") field = 4;
    else return false;
    if (seen & field) return false;
    seen |= field;
  } while (cursor.Consume(','));
  if (!cursor.Consume('}') || !cursor.AtEnd() || seen != 7) return false;
  operationId = parsed;
  return true;
}

PhotoPoll ParseImageOperation(const std::string& json, const std::string& operationId, const TurnCorrelation& expected) {
  if (json.empty() || json.size() > 4096 || !IsValidConversationUtf8(json, 4096, false)) return PhotoPoll::Failed;
  Cursor cursor(json.data(), json.size());
  uint8_t seen = 0;
  std::string status;
  bool result = false, error = false;
  if (!cursor.Consume('{')) return PhotoPoll::Failed;
  do {
    std::string key, value;
    uint8_t field = 0;
    if (!cursor.ReadString(key, 32, true) || !cursor.Consume(':')) return PhotoPoll::Failed;
    if (key == "correlation") { field = 8; if (!ReadCorrelation(cursor, expected)) return PhotoPoll::Failed; }
    else if (key == "result") { field = 16; if (!cursor.ReadNull()) { result = ReadResult(cursor); if (!result) return PhotoPoll::Failed; } }
    else if (key == "error") { field = 32; if (!cursor.ReadNull()) { error = true; if (!cursor.ReadObjectJson(value, 1024)) return PhotoPoll::Failed; } }
    else {
      if (!cursor.ReadString(value, 36, true)) return PhotoPoll::Failed;
      if (key == "operationId" && value == operationId) field = 1;
      else if (key == "kind" && value == "image") field = 2;
      else if (key == "status") { field = 4; status = value; }
      else return PhotoPoll::Failed;
    }
    if (seen & field) return PhotoPoll::Failed;
    seen |= field;
  } while (cursor.Consume(','));
  if (!cursor.Consume('}') || !cursor.AtEnd() || seen != 63) return PhotoPoll::Failed;
  if (status == "completed" && result && !error) return PhotoPoll::Completed;
  if ((status == "pending" || status == "running") && !result && !error) return PhotoPoll::Pending;
  return PhotoPoll::Failed;
}

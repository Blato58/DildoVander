#include "OperationAcceptedCodec.h"

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

  bool AtEnd() {
    SkipWhitespace();
    return position_ == length_;
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

}  // namespace

bool ParseOperationAccepted(const char* data, size_t length,
                            std::string& operationId) {
  operationId.clear();
  if (data == nullptr || length == 0 || length > 128) {
    return false;
  }
  Cursor cursor(data, length);
  std::string key;
  std::string parsed;
  if (!cursor.Consume('{') || !cursor.ReadString(key, 32) ||
      key != "operationId" || !cursor.Consume(':') ||
      !cursor.ReadString(parsed, 36) || !cursor.Consume('}') ||
      !cursor.AtEnd() || !IsConversationUuid(parsed)) {
    return false;
  }
  operationId = parsed;
  return true;
}

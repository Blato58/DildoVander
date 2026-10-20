#include "TokenResponseParser.h"

#include <stdint.h>

#include <string>

namespace {

class Cursor {
 public:
  Cursor(const char* data, size_t length) : data_(data), length_(length) {}

  void SkipWhitespace() {
    while (position_ < length_ &&
           (data_[position_] == ' ' || data_[position_] == '\r' ||
            data_[position_] == '\n' || data_[position_] == '\t')) {
      ++position_;
    }
  }

  bool Consume(char expected) {
    SkipWhitespace();
    if (position_ >= length_ || data_[position_] != expected) {
      return false;
    }
    ++position_;
    return true;
  }

  bool AtEnd() {
    SkipWhitespace();
    return position_ == length_;
  }

  bool NextIs(char expected) {
    SkipWhitespace();
    return position_ < length_ && data_[position_] == expected;
  }

  bool ReadString(std::string& value, size_t maximumLength) {
    SkipWhitespace();
    if (position_ >= length_ || data_[position_++] != '"') {
      return false;
    }
    value.clear();
    while (position_ < length_) {
      const unsigned char current =
          static_cast<unsigned char>(data_[position_++]);
      if (current == '"') {
        return true;
      }
      if (current < 0x20 || current > 0x7e || current == '\\' ||
          value.size() >= maximumLength) {
        return false;
      }
      value.push_back(static_cast<char>(current));
    }
    return false;
  }

  bool ReadPositiveInteger(uint32_t& value) {
    SkipWhitespace();
    if (position_ >= length_ || data_[position_] < '1' ||
        data_[position_] > '9') {
      return false;
    }
    uint32_t parsed = 0;
    while (position_ < length_ && data_[position_] >= '0' &&
           data_[position_] <= '9') {
      parsed = parsed * 10 +
               static_cast<uint32_t>(data_[position_++] - '0');
      if (parsed > 900) {
        return false;
      }
    }
    value = parsed;
    return true;
  }

 private:
  const char* data_;
  size_t length_;
  size_t position_ = 0;
};

struct ParsedResponse {
  ~ParsedResponse() {
    if (!value.accessToken.empty()) {
      volatile char* bytes = &value.accessToken[0];
      for (size_t index = 0; index < value.accessToken.size(); ++index) {
        bytes[index] = 0;
      }
    }
  }

  TokenExchangeResponse value;
};

}  // namespace

bool ParseTokenResponse(const char* data, size_t length,
                        TokenExchangeResponse& response) {
  response = {};
  if (data == nullptr || length == 0 || length > 512) {
    return false;
  }

  Cursor cursor(data, length);
  ParsedResponse parsed;
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
    if (key == "accessToken") {
      field = 1;
      if (!cursor.ReadString(parsed.value.accessToken, 256)) {
        return false;
      }
    } else if (key == "tokenType") {
      field = 2;
      if (!cursor.ReadString(parsed.value.tokenType, 16)) {
        return false;
      }
    } else if (key == "deviceId") {
      field = 4;
      if (!cursor.ReadString(parsed.value.deviceId, 64)) {
        return false;
      }
    } else if (key == "expiresInSeconds") {
      field = 8;
      if (!cursor.ReadPositiveInteger(parsed.value.expiresInSeconds)) {
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

  if (!cursor.Consume('}') || !cursor.AtEnd() || seen != 15) {
    return false;
  }
  parsed.value.status = TokenExchangeStatus::Success;
  response = parsed.value;
  return true;
}

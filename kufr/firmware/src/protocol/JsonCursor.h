#pragma once

#include <stddef.h>
#include <stdint.h>

#include <limits>
#include <algorithm>
#include <vector>
#include <string>

namespace kufr_protocol {

class JsonCursor {
 public:
  JsonCursor(const char* data, size_t length) : data_(data), length_(length) {}

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

  bool ReadTrue() {
    SkipWhitespace();
    if (length_ - position_ < 4 || data_[position_] != 't' ||
        data_[position_ + 1] != 'r' || data_[position_ + 2] != 'u' ||
        data_[position_ + 3] != 'e') {
      return false;
    }
    position_ += 4;
    return true;
  }

  bool ReadBoolean(bool& value) {
    if (ReadTrue()) { value = true; return true; }
    SkipWhitespace();
    if (length_ - position_ < 5 || std::string(data_ + position_, 5) != "false") return false;
    position_ += 5;
    value = false;
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

  // Capture a bounded object while checking duplicate keys and scalar syntax.
  // Used for nested backend correlation/result objects before their typed parser.
  bool ReadObjectJson(std::string& json, size_t maximumBytes, unsigned depth = 0) {
    SkipWhitespace();
    const size_t start = position_;
    if (depth >= 4 || !Consume('{')) return false;
    std::vector<std::string> keys;
    if (!NextIs('}')) {
      do {
        std::string key, value;
        uint64_t number = 0;
        if (!ReadString(key, 64) || !Consume(':') || keys.size() >= 16 ||
            std::find(keys.begin(), keys.end(), key) != keys.end()) return false;
        keys.push_back(key);
        if (NextIs('{')) {
          if (!ReadObjectJson(value, maximumBytes, depth + 1)) return false;
        } else if (NextIs('"')) {
          if (!ReadString(value, maximumBytes)) return false;
        } else if (NextIs('n')) {
          if (!ReadNull()) return false;
        } else if (!ReadUnsigned(number)) return false;
        if (position_ - start > maximumBytes) return false;
      } while (Consume(','));
    }
    if (!Consume('}') || position_ - start > maximumBytes) return false;
    json.assign(data_ + start, position_ - start);
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

}  // namespace kufr_protocol

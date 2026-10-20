#pragma once

#include <string>

inline void AppendJsonString(const std::string& value, std::string& output) {
  constexpr char HexDigits[] = "0123456789abcdef";
  output.push_back('"');
  for (unsigned char current : value) {
    if (current == '"' || current == '\\') {
      output.push_back('\\');
      output.push_back(static_cast<char>(current));
    } else if (current < 0x20) {
      output += "\\u00";
      output.push_back(HexDigits[current >> 4]);
      output.push_back(HexDigits[current & 0x0f]);
    } else {
      output.push_back(static_cast<char>(current));
    }
  }
  output.push_back('"');
}

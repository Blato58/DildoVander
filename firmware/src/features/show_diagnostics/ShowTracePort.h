#pragma once

#include <string>
#include <stdint.h>

// Observer-only boundary. Implementations cannot accept control commands.
class ShowTraceReadPort {
 public:
  virtual ~ShowTraceReadPort() = default;
  virtual std::string ReadTrace(uint64_t nowMs) = 0;
};

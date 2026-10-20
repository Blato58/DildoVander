#pragma once

#include <stddef.h>
#include <stdint.h>

enum class JournalReadResult : uint8_t {
  Missing,
  Found,
  Failed,
};

class JournalStorage {
 public:
  virtual ~JournalStorage() {}

  virtual JournalReadResult Read(uint8_t* data, size_t capacity,
                                 size_t& length) = 0;
  virtual bool Write(const uint8_t* data, size_t length) = 0;
};

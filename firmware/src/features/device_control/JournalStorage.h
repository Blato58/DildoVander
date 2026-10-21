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
  // Replacement must expose either the complete old record or the complete new
  // record after a power loss. False may also mean a committed write whose ACK
  // was lost: stop admissions and reopen storage before deciding what to retry.
  virtual bool Write(const uint8_t* data, size_t length) = 0;
};

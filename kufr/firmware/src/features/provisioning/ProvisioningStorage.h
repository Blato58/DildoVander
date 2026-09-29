#pragma once

#include <stddef.h>
#include <stdint.h>

enum class ProvisioningReadResult {
  Missing,
  Found,
  Failed,
};

class ProvisioningStorage {
 public:
  virtual ~ProvisioningStorage() = default;
  virtual ProvisioningReadResult Read(uint8_t* data, size_t capacity,
                                      size_t& length) = 0;
  virtual bool Write(const uint8_t* data, size_t length) = 0;
};

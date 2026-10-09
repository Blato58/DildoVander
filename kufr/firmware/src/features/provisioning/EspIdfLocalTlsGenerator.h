#pragma once

#include <string>
#include "LocalTlsIdentity.h"

// Generate once at boot, with the bootloader entropy source enabled and before
// Wi-Fi/I2S starts. NVS persistence belongs to LocalTlsIdentity.
class EspIdfLocalTlsGenerator final : public LocalTlsGenerator {
 public:
  explicit EspIdfLocalTlsGenerator(const std::string& hostname) : hostname_(hostname) {}
  bool Generate(std::vector<uint8_t>& certificate,
                std::vector<uint8_t>& privateKey) override;
 private:
  std::string hostname_;
};

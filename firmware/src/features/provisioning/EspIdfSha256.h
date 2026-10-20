#pragma once

#include "LocalPairingCredential.h"

class EspIdfSha256 final : public Sha256Port {
 public:
  EspIdfSha256();

  bool IsHealthy() const;
  bool Hash(const uint8_t* data, size_t length,
            std::array<uint8_t, 32>& digest) override;

 private:
  bool initialized_ = false;
};

#pragma once

#include "LocalTlsIdentity.h"

class EspIdfLocalTlsValidator final : public LocalTlsValidator {
 public:
  bool Validate(const uint8_t* certificate, size_t certificateLength,
                const uint8_t* privateKey, size_t privateKeyLength,
                std::array<uint8_t, 32>& publicKeyDigest) override;
};

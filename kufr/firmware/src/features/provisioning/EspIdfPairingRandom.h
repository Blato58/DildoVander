#pragma once

#include "LocalPairingCredential.h"

// Currently supports connected STA only. Service AP entropy needs its own
// lifecycle gate before that provisioning path can issue a token.
class EspIdfPairingRandom final : public PairingRandomPort {
 public:
  bool Fill(uint8_t* output, size_t length) override;
};

#pragma once

#include <stddef.h>

class LocalPairingCredential;

// Validates the Authorization value from an already established TLS handshake.
class LocalBearerAuthenticator final {
 public:
  explicit LocalBearerAuthenticator(LocalPairingCredential& credential);

  bool Authenticate(const char* authorization, size_t length);

 private:
  LocalPairingCredential& credential_;
};

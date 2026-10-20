#include "LocalBearerAuthenticator.h"

#include <array>
#include <cstring>

#include "../provisioning/LocalPairingCredential.h"

namespace {

constexpr char BearerPrefix[] = "Bearer ";
constexpr size_t PrefixSize = sizeof(BearerPrefix) - 1;
constexpr size_t EncodedTokenSize = LocalPairingCredential::TokenSize * 2;

int HexDigit(char value) {
  if (value >= '0' && value <= '9') {
    return value - '0';
  }
  if (value >= 'a' && value <= 'f') {
    return value - 'a' + 10;
  }
  return -1;
}

class TokenBuffer final {
 public:
  ~TokenBuffer() {
    volatile uint8_t* bytes = value.data();
    for (size_t index = 0; index < value.size(); ++index) {
      bytes[index] = 0;
    }
  }

  std::array<uint8_t, LocalPairingCredential::TokenSize> value{};
};

}  // namespace

LocalBearerAuthenticator::LocalBearerAuthenticator(
    LocalPairingCredential& credential)
    : credential_(credential) {}

bool LocalBearerAuthenticator::Authenticate(const char* authorization,
                                            size_t length) {
  if (authorization == nullptr ||
      length != PrefixSize + EncodedTokenSize ||
      std::memcmp(authorization, BearerPrefix, PrefixSize) != 0) {
    return false;
  }

  TokenBuffer token;
  for (size_t index = 0; index < token.value.size(); ++index) {
    const int high = HexDigit(authorization[PrefixSize + index * 2]);
    const int low = HexDigit(authorization[PrefixSize + index * 2 + 1]);
    if (high < 0 || low < 0) {
      return false;
    }
    token.value[index] = static_cast<uint8_t>((high << 4) | low);
  }
  return credential_.VerifyToken(token.value.data(), token.value.size());
}

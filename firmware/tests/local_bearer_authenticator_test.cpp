#include <cassert>

#include <algorithm>
#include <array>
#include <string>

#include "../src/features/local_control/LocalBearerAuthenticator.h"
#include "../src/features/provisioning/LocalPairingCredential.h"

namespace {

class MemoryStorage final : public LocalPairingStorage {
 public:
  ProvisioningReadResult Read(uint8_t*, size_t, size_t&) override {
    return ProvisioningReadResult::Missing;
  }

  bool Write(const uint8_t*, size_t length) override {
    return length == LocalPairingCredential::RecordSize;
  }
};

class TestHasher final : public Sha256Port {
 public:
  bool Hash(const uint8_t* data, size_t length,
            std::array<uint8_t, 32>& digest) override {
    digest.fill(0x51);
    for (size_t index = 0; index < length; ++index) {
      digest[index % digest.size()] = static_cast<uint8_t>(
          digest[index % digest.size()] ^ data[index] ^ index);
    }
    return true;
  }
};

class TestRandom final : public PairingRandomPort {
 public:
  bool Fill(uint8_t* output, size_t length) override {
    if (length != LocalPairingCredential::TokenSize) {
      return false;
    }
    for (size_t index = 0; index < length; ++index) {
      output[index] = static_cast<uint8_t>(index);
    }
    return true;
  }
};

class TestGate final : public ServiceGate {
 public:
  bool ServiceModeActive() const override { return true; }
  bool ActuatorSupplyDisconnected() const override { return true; }
};

std::string EncodeAuthorization(
    const std::array<uint8_t, LocalPairingCredential::TokenSize>& token) {
  constexpr char Digits[] = "0123456789abcdef";
  std::string result = "Bearer ";
  for (uint8_t value : token) {
    result.push_back(Digits[value >> 4]);
    result.push_back(Digits[value & 0x0f]);
  }
  return result;
}

void TestExactBearerEncoding() {
  MemoryStorage storage;
  TestHasher hasher;
  TestRandom random;
  TestGate gate;
  LocalPairingCredential credential(storage, hasher, random);
  LocalBearerAuthenticator authenticator(credential);
  std::array<uint8_t, 16> secret{};
  secret.fill(0x3d);
  std::array<uint8_t, LocalPairingCredential::TokenSize> token{};
  assert(credential.ProvisionPairingSecret(secret.data(), secret.size(), gate));
  assert(credential.Pair(secret.data(), secret.size(), token));

  const std::string valid = EncodeAuthorization(token);
  assert(authenticator.Authenticate(valid.data(), valid.size()));
  assert(!authenticator.Authenticate(nullptr, valid.size()));
  assert(!authenticator.Authenticate(valid.data(), valid.size() - 1));
  assert(!authenticator.Authenticate(valid.data(), valid.size() + 1));

  std::string changed = valid;
  changed[0] = 'b';
  assert(!authenticator.Authenticate(changed.data(), changed.size()));
  changed = valid;
  changed[7] = 'F';
  assert(!authenticator.Authenticate(changed.data(), changed.size()));
  changed = valid;
  changed[7] = 'g';
  assert(!authenticator.Authenticate(changed.data(), changed.size()));
  changed = valid;
  changed[8] = valid[8] == '0' ? '1' : '0';
  assert(!authenticator.Authenticate(changed.data(), changed.size()));
}

}  // namespace

int main() {
  TestExactBearerEncoding();
  return 0;
}

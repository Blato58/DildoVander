#include <cassert>

#include <array>
#include <string>

#include "../src/features/local_control/LocalBearerAuthenticator.h"
#include "../src/features/local_control/LocalSocketAdmission.h"
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
    digest.fill(0x73);
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
      output[index] = static_cast<uint8_t>(0xa0 + index);
    }
    return true;
  }
};

class TestGate final : public ServiceGate {
 public:
  bool ServiceModeActive() const override { return true; }
  bool ActuatorSupplyDisconnected() const override { return true; }
};

std::string Authorization(
    const std::array<uint8_t, LocalPairingCredential::TokenSize>& token) {
  constexpr char Digits[] = "0123456789abcdef";
  std::string result = "Bearer ";
  for (uint8_t value : token) {
    result.push_back(Digits[value >> 4]);
    result.push_back(Digits[value & 0x0f]);
  }
  return result;
}

void TestExclusiveHandshakeAndDisconnect() {
  MemoryStorage storage;
  TestHasher hasher;
  TestRandom random;
  TestGate gate;
  LocalPairingCredential credential(storage, hasher, random);
  LocalBearerAuthenticator authenticator(credential);
  LocalSocketAdmission admission(authenticator);
  const std::array<uint8_t, 16> secret = {
      1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16};
  std::array<uint8_t, LocalPairingCredential::TokenSize> token{};
  assert(credential.ProvisionPairingSecret(secret.data(), secret.size(), gate));
  assert(credential.Pair(secret.data(), secret.size(), token));
  const std::string authorization = Authorization(token);
  LocalSocketAdmission::Handle first;
  LocalSocketAdmission::Handle second;

  assert(!admission.Reserve(-1, authorization.data(), authorization.size(), 0,
                            first));
  assert(!admission.Reserve(7, nullptr, 0, 0, first));
  assert(admission.Reserve(7, authorization.data(), authorization.size(), 1,
                           first));
  assert(!admission.IsActive(first));
  assert(!admission.Reserve(8, authorization.data(), authorization.size(), 2,
                            second));
  assert(!admission.Confirm(second, 3));
  assert(admission.Confirm(first, 4));
  assert(admission.IsActive(first));
  assert(!admission.IsActive(second));
  assert(admission.Release(second) ==
         LocalSocketAdmission::ReleaseResult::None);
  assert(!admission.Reserve(8, authorization.data(), authorization.size(), 5,
                            second));
  assert(admission.Release(first) ==
         LocalSocketAdmission::ReleaseResult::Active);
  assert(!admission.IsActive(first));
  assert(admission.Reserve(8, authorization.data(), authorization.size(), 6,
                           second));
  assert(admission.Confirm(second, 7));
  assert(admission.Release(second) ==
         LocalSocketAdmission::ReleaseResult::Active);
  assert(admission.Reserve(9, authorization.data(), authorization.size(), 8,
                           second));
  assert(admission.Release(second) ==
         LocalSocketAdmission::ReleaseResult::Reserved);
}

void TestExpiredReservationCannotBecomeOwner() {
  MemoryStorage storage;
  TestHasher hasher;
  TestRandom random;
  TestGate gate;
  LocalPairingCredential credential(storage, hasher, random);
  LocalBearerAuthenticator authenticator(credential);
  LocalSocketAdmission admission(authenticator);
  std::array<uint8_t, 16> secret{};
  secret.fill(0x49);
  std::array<uint8_t, LocalPairingCredential::TokenSize> token{};
  assert(credential.ProvisionPairingSecret(secret.data(), secret.size(), gate));
  assert(credential.Pair(secret.data(), secret.size(), token));
  const std::string authorization = Authorization(token);
  LocalSocketAdmission::Handle first;
  LocalSocketAdmission::Handle second;
  LocalSocketAdmission::Handle expired;

  assert(admission.Reserve(11, authorization.data(), authorization.size(), 100,
                           first));
  assert(!admission.Confirm(first, 10100));
  assert(!admission.IsActive(first));
  assert(!admission.Reserve(12, authorization.data(), authorization.size(),
                            10101, second));
  assert(admission.ExpirePending(10100, expired));
  assert(expired.socket == first.socket);
  assert(expired.generation == first.generation);
  assert(admission.Reserve(12, authorization.data(), authorization.size(),
                           10101, second));
  assert(!admission.Confirm(first, 10102));
  assert(admission.Confirm(second, 10103));
  assert(admission.Release(second) ==
         LocalSocketAdmission::ReleaseResult::Active);

  assert(admission.Reserve(13, authorization.data(), authorization.size(),
                           20000, first));
  assert(admission.ExpirePending(30000, expired));
  assert(admission.Reserve(13, authorization.data(), authorization.size(),
                           30001, second));
  assert(first.generation != second.generation);
  assert(!admission.Confirm(first, 30002));
  assert(admission.Release(first) ==
         LocalSocketAdmission::ReleaseResult::None);
  assert(admission.Confirm(second, 30002));
  assert(admission.IsActive(second));
}

}  // namespace

int main() {
  TestExclusiveHandshakeAndDisconnect();
  TestExpiredReservationCannotBecomeOwner();
  return 0;
}

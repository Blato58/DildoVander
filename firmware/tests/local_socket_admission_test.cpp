#include <cassert>

#include <array>
#include <atomic>
#include <chrono>
#include <future>
#include <functional>
#include <thread>
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
    if (beforeWrite) beforeWrite();
    return length == LocalPairingCredential::RecordSize;
  }
  std::function<void()> beforeWrite;
};

class TestHasher final : public Sha256Port {
 public:
  bool Hash(const uint8_t* data, size_t length,
            std::array<uint8_t, 32>& digest) override {
    ++calls;
    if (beforeHash) beforeHash();
    digest.fill(0x73);
    for (size_t index = 0; index < length; ++index) {
      digest[index % digest.size()] = static_cast<uint8_t>(
          digest[index % digest.size()] ^ data[index] ^ index);
    }
    return true;
  }
  std::atomic<unsigned> calls{0};
  std::function<void()> beforeHash;
};

class TestRandom final : public PairingRandomPort {
 public:
  bool Fill(uint8_t* output, size_t length) override {
    if (length != LocalPairingCredential::TokenSize) {
      return false;
    }
    for (size_t index = 0; index < length; ++index) {
      output[index] = static_cast<uint8_t>(0xa0 + index + offset);
    }
    return true;
  }
  uint8_t offset = 0;
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

void TestRecoveryGateLinearizesAuthentication() {
  MemoryStorage storage;
  TestHasher hasher;
  TestRandom random;
  LocalPairingCredential credential(storage, hasher, random);
  LocalBearerAuthenticator authenticator(credential);
  LocalSocketAdmission admission(authenticator);
  std::array<uint8_t, LocalPairingCredential::TokenSize> token{};
  assert(credential.InitializeFromAuthenticatedBle(token));
  const std::string originalAuthorization = Authorization(token);
  LocalSocketAdmission::Handle handle;
  assert(admission.Reserve(21, originalAuthorization.data(), originalAuthorization.size(), 0, handle));
  assert(!admission.RunIfEmpty([&]() { assert(false); }));
  assert(admission.Confirm(handle, 1));
  assert(!admission.RunIfEmpty([&]() { assert(false); }));
  assert(admission.Release(handle) == LocalSocketAdmission::ReleaseResult::Active);

  const auto previous = handle;
  std::promise<void> storageEntered;
  std::promise<void> releaseStorage;
  auto storageEnteredFuture = storageEntered.get_future();
  auto releaseStorageFuture = releaseStorage.get_future();
  storage.beforeWrite = [&]() { storageEntered.set_value(); releaseStorageFuture.wait(); };
  random.offset = 1;
  std::thread rotation([&]() {
    assert(admission.RunIfEmpty([&]() {
      assert(credential.RotateFromAuthenticatedBle(token));
    }));
  });
  storageEnteredFuture.wait(); // Rotation now holds the credential lock inside I/O.
  const unsigned hashesWhileStalled = hasher.calls.load();
  std::promise<void> transportCompleted;
  auto transportCompletedFuture = transportCompleted.get_future();
  std::thread transport([&]() {
    LocalSocketAdmission::Handle rejected{99, 99};
    assert(admission.TryReserve(22, originalAuthorization.data(), originalAuthorization.size(), 2, rejected) ==
        LocalSocketAdmission::ReserveResult::Busy);
    assert(rejected.socket == -1 && rejected.generation == 0);
    assert(admission.TryReserve(22, nullptr, 0, 2, rejected) == LocalSocketAdmission::ReserveResult::Busy);
    assert(!admission.Confirm(previous, 2) && !admission.IsActive(previous));
    assert(admission.Release(previous) == LocalSocketAdmission::ReleaseResult::None);
    assert(!admission.ExpirePending(20000, rejected));
    assert(!admission.RunIfEmpty([&]() { assert(false); }));
    transportCompleted.set_value();
  });
  assert(transportCompletedFuture.wait_for(std::chrono::seconds(2)) == std::future_status::ready);
  assert(hasher.calls.load() == hashesWhileStalled); // No credential/authentication call.
  transport.join();
  releaseStorage.set_value();
  rotation.join();
  storage.beforeWrite = nullptr;

  const std::string recoveredAuthorization = Authorization(token);
  assert(admission.TryReserve(21, originalAuthorization.data(), originalAuthorization.size(), 3, handle) ==
      LocalSocketAdmission::ReserveResult::Unauthorized);
  assert(admission.Reserve(21, recoveredAuthorization.data(), recoveredAuthorization.size(), 3, handle));
  assert(handle.generation == previous.generation + 1); // Busy never allocates a generation.
  assert(!admission.Confirm(previous, 4));
  assert(admission.Release(previous) == LocalSocketAdmission::ReleaseResult::None);
  assert(!admission.RunIfEmpty([&]() { assert(false); }));
  assert(admission.Release(handle) == LocalSocketAdmission::ReleaseResult::Reserved);
  assert(admission.RunIfEmpty([&]() {}));
  bool threw = false;
  try {
    admission.RunIfEmpty([&]() { throw 7; });
  } catch (int value) { threw = value == 7; }
  assert(threw && admission.RunIfEmpty([&]() {}));
  assert(admission.Reserve(24, recoveredAuthorization.data(), recoveredAuthorization.size(), 5, handle));
  assert(admission.Release(handle) == LocalSocketAdmission::ReleaseResult::Reserved);
}

void TestAuthenticationWinsBeforeRecoveryReservation() {
  MemoryStorage storage;
  TestHasher hasher;
  TestRandom random;
  LocalPairingCredential credential(storage, hasher, random);
  LocalBearerAuthenticator authenticator(credential);
  LocalSocketAdmission admission(authenticator);
  std::array<uint8_t, LocalPairingCredential::TokenSize> token{};
  assert(credential.InitializeFromAuthenticatedBle(token));
  const auto authorization = Authorization(token);
  std::promise<void> hashing;
  std::promise<void> releaseHash;
  auto hashingFuture = hashing.get_future();
  auto releaseHashFuture = releaseHash.get_future();
  hasher.beforeHash = [&]() { hashing.set_value(); releaseHashFuture.wait(); };
  LocalSocketAdmission::Handle admitted;
  std::thread handshake([&]() {
    assert(admission.Reserve(31, authorization.data(), authorization.size(), 1, admitted));
  });
  hashingFuture.wait();
  std::promise<void> recoveryAttempting;
  std::promise<void> recoveryCompleted;
  auto recoveryAttemptingFuture = recoveryAttempting.get_future();
  auto recoveryCompletedFuture = recoveryCompleted.get_future();
  bool recovered = true;
  std::thread recovery([&]() {
    recoveryAttempting.set_value();
    recovered = admission.RunIfEmpty([&]() { assert(false); });
    recoveryCompleted.set_value();
  });
  recoveryAttemptingFuture.wait();
  assert(recoveryCompletedFuture.wait_for(std::chrono::milliseconds(20)) == std::future_status::timeout);
  releaseHash.set_value();
  handshake.join();
  recovery.join();
  hasher.beforeHash = nullptr;
  assert(!recovered && admitted.generation == 1);
  assert(admission.Confirm(admitted, 2));
  assert(admission.Release(admitted) == LocalSocketAdmission::ReleaseResult::Active);
  assert(admission.RunIfEmpty([&]() {}));
}

}  // namespace

int main() {
  TestExclusiveHandshakeAndDisconnect();
  TestExpiredReservationCannotBecomeOwner();
  TestRecoveryGateLinearizesAuthentication();
  TestAuthenticationWinsBeforeRecoveryReservation();
  return 0;
}

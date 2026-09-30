#include <cassert>

#include <algorithm>
#include <array>

#include "../src/features/provisioning/LocalPairingCredential.h"

namespace {

class MemoryStorage final : public LocalPairingStorage {
 public:
  ProvisioningReadResult Read(uint8_t* output, size_t capacity,
                              size_t& length) override {
    if (failRead) {
      return ProvisioningReadResult::Failed;
    }
    if (!present) {
      return ProvisioningReadResult::Missing;
    }
    if (capacity < storedLength) {
      return ProvisioningReadResult::Failed;
    }
    std::copy(record.begin(), record.begin() + storedLength, output);
    length = storedLength;
    return ProvisioningReadResult::Found;
  }

  bool Write(const uint8_t* input, size_t length) override {
    ++writes;
    if (failWrite || length != record.size()) {
      return false;
    }
    std::copy(input, input + length, record.begin());
    storedLength = length;
    present = true;
    return true;
  }

  std::array<uint8_t, LocalPairingCredential::RecordSize> record{};
  size_t storedLength = 0;
  int writes = 0;
  bool present = false;
  bool failRead = false;
  bool failWrite = false;
};

class FakeHasher final : public Sha256Port {
 public:
  bool Hash(const uint8_t* data, size_t length,
            std::array<uint8_t, 32>& digest) override {
    if (fail || data == nullptr) {
      return false;
    }
    for (size_t index = 0; index < digest.size(); ++index) {
      digest[index] = static_cast<uint8_t>(0x40 + index + length);
    }
    for (size_t index = 0; index < length; ++index) {
      digest[index % digest.size()] = static_cast<uint8_t>(
          digest[index % digest.size()] ^ data[index] ^
          static_cast<uint8_t>(index));
    }
    return true;
  }

  bool fail = false;
};

class FakeRandom final : public PairingRandomPort {
 public:
  bool Fill(uint8_t* output, size_t length) override {
    ++calls;
    if (fail || length != LocalPairingCredential::TokenSize) {
      return false;
    }
    for (size_t index = 0; index < length; ++index) {
      output[index] = zero ? 0 : static_cast<uint8_t>(0x80 + index);
    }
    return true;
  }

  int calls = 0;
  bool fail = false;
  bool zero = false;
};

class FakeServiceGate final : public ServiceGate {
 public:
  bool ServiceModeActive() const override { return serviceMode; }
  bool ActuatorSupplyDisconnected() const override {
    return supplyDisconnected;
  }

  bool serviceMode = false;
  bool supplyDisconnected = false;
};

std::array<uint8_t, 16> Secret(uint8_t value) {
  std::array<uint8_t, 16> result{};
  result.fill(value);
  return result;
}

void TestOneTimePairingAndRestart() {
  MemoryStorage storage;
  FakeHasher hasher;
  FakeRandom random;
  FakeServiceGate gate;
  LocalPairingCredential credential(storage, hasher, random);
  const auto secret = Secret(0x39);
  const auto wrongSecret = Secret(0x42);
  std::array<uint8_t, LocalPairingCredential::TokenSize> token{};

  assert(credential.IsHealthy());
  assert(!credential.IsPairingReady());
  assert(!credential.Pair(secret.data(), secret.size(), token));
  assert(!credential.ProvisionPairingSecret(secret.data(), secret.size(), gate));
  gate.serviceMode = true;
  assert(!credential.ProvisionPairingSecret(secret.data(), secret.size(), gate));
  gate.supplyDisconnected = true;
  assert(credential.ProvisionPairingSecret(secret.data(), secret.size(), gate));
  assert(credential.IsPairingReady());
  assert(storage.writes == 1);
  assert(std::search(storage.record.begin(), storage.record.end(),
                     secret.begin(), secret.end()) == storage.record.end());

  assert(!credential.Pair(wrongSecret.data(), wrongSecret.size(), token));
  assert(random.calls == 0);
  assert(credential.Pair(secret.data(), secret.size(), token));
  assert(credential.IsPaired());
  assert(!credential.IsPairingReady());
  assert(storage.writes == 2);
  assert(std::search(storage.record.begin(), storage.record.end(),
                     token.begin(), token.end()) == storage.record.end());
  assert(credential.VerifyToken(token.data(), token.size()));
  assert(!credential.VerifyToken(token.data(), token.size() - 1));
  assert(!credential.Pair(secret.data(), secret.size(), token));
  assert(std::all_of(token.begin(), token.end(),
                     [](uint8_t value) { return value == 0; }));

  LocalPairingCredential restarted(storage, hasher, random);
  std::array<uint8_t, LocalPairingCredential::TokenSize> originalToken{};
  for (size_t index = 0; index < originalToken.size(); ++index) {
    originalToken[index] = static_cast<uint8_t>(0x80 + index);
  }
  assert(restarted.IsPaired());
  assert(restarted.VerifyToken(originalToken.data(), originalToken.size()));
  assert(!restarted.VerifyToken(wrongSecret.data(), wrongSecret.size()));

  gate.serviceMode = false;
  assert(!restarted.ProvisionPairingSecret(
      wrongSecret.data(), wrongSecret.size(), gate));
  gate.serviceMode = true;
  assert(restarted.ProvisionPairingSecret(
      wrongSecret.data(), wrongSecret.size(), gate));
  assert(restarted.IsPairingReady());
  assert(!restarted.VerifyToken(originalToken.data(), originalToken.size()));
}

void TestFailureAndMalformedRecordFailClosed() {
  MemoryStorage storage;
  FakeHasher hasher;
  FakeRandom random;
  FakeServiceGate gate;
  gate.serviceMode = true;
  gate.supplyDisconnected = true;
  const auto secret = Secret(0x51);
  std::array<uint8_t, LocalPairingCredential::TokenSize> token{};

  LocalPairingCredential initial(storage, hasher, random);
  assert(initial.ProvisionPairingSecret(secret.data(), secret.size(), gate));
  random.fail = true;
  assert(!initial.Pair(secret.data(), secret.size(), token));
  assert(initial.IsPairingReady());
  random.fail = false;
  random.zero = true;
  assert(!initial.Pair(secret.data(), secret.size(), token));
  random.zero = false;
  storage.failWrite = true;
  assert(!initial.Pair(secret.data(), secret.size(), token));
  assert(!initial.IsHealthy());
  assert(!initial.IsPaired());
  assert(std::all_of(token.begin(), token.end(),
                     [](uint8_t value) { return value == 0; }));

  storage.failWrite = false;
  const auto validRecord = storage.record;
  const auto rejectCorruptRecord = [&](size_t index, uint8_t value) {
    storage.record = validRecord;
    storage.record[index] = value;
    LocalPairingCredential corrupted(storage, hasher, random);
    assert(!corrupted.IsHealthy());
    assert(!corrupted.IsPairingReady());
    assert(!corrupted.ProvisionPairingSecret(
        secret.data(), secret.size(), gate));
  };
  rejectCorruptRecord(0, 'X');
  rejectCorruptRecord(4, 9);
  rejectCorruptRecord(5, 1);
  rejectCorruptRecord(38, 1);
  storage.record = validRecord;
  std::fill(storage.record.begin() + 6,
            storage.record.begin() + 38, static_cast<uint8_t>(0));
  LocalPairingCredential missingDigest(storage, hasher, random);
  assert(!missingDigest.IsHealthy());
  storage.record = validRecord;
  --storage.storedLength;
  LocalPairingCredential truncated(storage, hasher, random);
  assert(!truncated.IsHealthy());
  storage.storedLength = validRecord.size();
  storage.failRead = true;
  LocalPairingCredential unreadable(storage, hasher, random);
  assert(!unreadable.IsHealthy());
}

}  // namespace

int main() {
  TestOneTimePairingAndRestart();
  TestFailureAndMalformedRecordFailClosed();
  return 0;
}

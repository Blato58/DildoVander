#include <cassert>

#include <algorithm>
#include <cstring>
#include <vector>

#include "../src/features/provisioning/LocalTlsIdentity.h"

namespace {

constexpr char Certificate[] = "-----BEGIN CERTIFICATE-----\nTEST\n-----END CERTIFICATE-----\n";
constexpr char PrivateKey[] = "-----BEGIN PRIVATE KEY-----\nTEST\n-----END PRIVATE KEY-----\n";

class MemoryStorage final : public ProvisioningStorage {
 public:
  ProvisioningReadResult Read(uint8_t* output, size_t capacity,
                              size_t& length) override {
    if (failRead || bytes.size() > capacity) {
      return ProvisioningReadResult::Failed;
    }
    if (bytes.empty()) {
      return ProvisioningReadResult::Missing;
    }
    std::copy(bytes.begin(), bytes.end(), output);
    length = bytes.size();
    return ProvisioningReadResult::Found;
  }
  bool Write(const uint8_t* input, size_t length) override {
    ++writes;
    bytes.assign(input, input + length);
    return !failWrite;
  }

  std::vector<uint8_t> bytes;
  bool failRead = false;
  bool failWrite = false;
  int writes = 0;
};

class Validator final : public LocalTlsValidator {
 public:
  bool Validate(const uint8_t*, size_t, const uint8_t*, size_t,
                std::array<uint8_t, 32>& digest) override {
    ++calls;
    digest.fill(zeroDigest ? 0 : 0x71);
    return !reject;
  }
  bool reject = false;
  bool zeroDigest = false;
  int calls = 0;
};

class Gate final : public ServiceGate {
 public:
  bool ServiceModeActive() const override { return service; }
  bool ActuatorSupplyDisconnected() const override { return disconnected; }
  bool service = false;
  bool disconnected = false;
};

class Generator final : public LocalTlsGenerator {
 public:
  bool Generate(std::vector<uint8_t>& certificate, std::vector<uint8_t>& key) override {
    ++calls;
    certificate.assign(Certificate, Certificate + sizeof(Certificate));
    key.assign(PrivateKey, PrivateKey + sizeof(PrivateKey));
    return !fail;
  }
  int calls = 0;
  bool fail = false;
};

void TestFirstInitializationPreservesRecords() {
  MemoryStorage storage;
  Validator validator;
  Generator generator;
  LocalTlsIdentity identity(storage, validator);
  assert(identity.InitializeIfMissing(generator));
  assert(identity.IsProvisioned() && generator.calls == 1 && storage.writes == 1);
  const auto record = storage.bytes;
  assert(identity.InitializeIfMissing(generator));
  LocalTlsIdentity restarted(storage, validator);
  assert(restarted.InitializeIfMissing(generator));
  assert(generator.calls == 1 && storage.writes == 1 && storage.bytes == record);
  Gate gate;
  assert(!identity.Provision(reinterpret_cast<const uint8_t*>(Certificate), sizeof(Certificate),
      reinterpret_cast<const uint8_t*>(PrivateKey), sizeof(PrivateKey), gate));
  assert(storage.bytes == record);
  storage.bytes[0] = 'X';
  LocalTlsIdentity corrupt(storage, validator);
  assert(!corrupt.InitializeIfMissing(generator));
  storage.failRead = true;
  LocalTlsIdentity unreadable(storage, validator);
  assert(!unreadable.InitializeIfMissing(generator));
  assert(generator.calls == 1 && storage.writes == 1);

  MemoryStorage failedStorage;
  LocalTlsIdentity failed(failedStorage, validator);
  generator.fail = true;
  assert(!failed.InitializeIfMissing(generator));
  assert(failed.IsHealthy() && failedStorage.writes == 0);
  generator.fail = false;
  failedStorage.failWrite = true;
  assert(!failed.InitializeIfMissing(generator));
  assert(!failed.IsHealthy() && failedStorage.writes == 1);
  const int calls = generator.calls;
  assert(!failed.InitializeIfMissing(generator));
  assert(generator.calls == calls && failedStorage.writes == 1);
}

bool Provision(LocalTlsIdentity& identity, const Gate& gate) {
  return identity.Provision(
      reinterpret_cast<const uint8_t*>(Certificate), sizeof(Certificate),
      reinterpret_cast<const uint8_t*>(PrivateKey), sizeof(PrivateKey), gate);
}

void TestServiceGateAndRestart() {
  MemoryStorage storage;
  Validator validator;
  Gate gate;
  LocalTlsIdentity identity(storage, validator);
  assert(identity.IsHealthy() && !identity.IsProvisioned());
  assert(identity.PrivateKey() == nullptr && identity.PrivateKeyLength() == 0);
  assert(!Provision(identity, gate));
  gate.service = true;
  assert(!Provision(identity, gate));
  gate.disconnected = true;
  assert(Provision(identity, gate));
  assert(storage.writes == 1);
  assert(identity.IsProvisioned());
  assert(identity.CertificateLength() == sizeof(Certificate));
  assert(identity.PrivateKeyLength() == sizeof(PrivateKey));
  assert(std::memcmp(identity.PrivateKey(), PrivateKey, sizeof(PrivateKey)) == 0);
  LocalTlsIdentity restarted(storage, validator);
  assert(restarted.IsProvisioned());
  assert(restarted.PublicKeyDigest() == identity.PublicKeyDigest());

  validator.reject = true;
  assert(!Provision(identity, gate));
  assert(identity.IsProvisioned() && storage.writes == 1);
  LocalTlsIdentity mismatched(storage, validator);
  assert(!mismatched.IsHealthy() && !mismatched.IsProvisioned());
}

void TestMalformedRecordsAndFailedWrite() {
  MemoryStorage storage;
  Validator validator;
  Gate gate;
  gate.service = true;
  gate.disconnected = true;
  LocalTlsIdentity identity(storage, validator);
  assert(Provision(identity, gate));
  const auto validRecord = storage.bytes;
  for (size_t length : {size_t{1}, size_t{7}, validRecord.size() - 1}) {
    storage.bytes.assign(validRecord.begin(), validRecord.begin() + length);
    LocalTlsIdentity truncated(storage, validator);
    assert(!truncated.IsHealthy());
  }
  storage.bytes = validRecord;
  storage.bytes[0] = 'X';
  LocalTlsIdentity wrongMagic(storage, validator);
  assert(!wrongMagic.IsHealthy());
  storage.bytes = validRecord;
  storage.bytes[4] = 0xff;
  storage.bytes[5] = 0xff;
  LocalTlsIdentity oversize(storage, validator);
  assert(!oversize.IsHealthy());
  storage.bytes = validRecord;
  storage.bytes[LocalTlsIdentity::HeaderSize + 20] = 0;
  LocalTlsIdentity embeddedNull(storage, validator);
  assert(!embeddedNull.IsHealthy());
  storage.bytes = validRecord;
  validator.zeroDigest = true;
  LocalTlsIdentity zeroPin(storage, validator);
  assert(!zeroPin.IsHealthy());
  validator.zeroDigest = false;
  storage.failWrite = true;
  assert(!Provision(identity, gate));
  assert(!identity.IsHealthy() && !identity.IsProvisioned());
  assert(identity.Certificate() == nullptr && identity.PrivateKey() == nullptr);
  assert(!Provision(identity, gate));
  // Failure must not promise rollback: the storage fake committed before
  // reporting the fault, just as an NVS replacement can fail during cleanup.
  storage.failWrite = false;
  LocalTlsIdentity recovered(storage, validator);
  assert(recovered.IsHealthy() && recovered.IsProvisioned());
  assert(recovered.PublicKeyDigest().front() == 0x71);
}

void TestInputBoundsAndMissingStorage() {
  MemoryStorage storage;
  Validator validator;
  Gate gate;
  gate.service = true;
  gate.disconnected = true;
  LocalTlsIdentity identity(storage, validator);
  const auto* certificate = reinterpret_cast<const uint8_t*>(Certificate);
  const auto* key = reinterpret_cast<const uint8_t*>(PrivateKey);
  assert(!identity.Provision(
      nullptr, sizeof(Certificate), key, sizeof(PrivateKey), gate));
  assert(!identity.Provision(
      certificate, sizeof(Certificate) - 1, key, sizeof(PrivateKey), gate));
  assert(!identity.Provision(
      certificate, LocalTlsIdentity::MaximumCertificatePemSize + 1,
      key, sizeof(PrivateKey), gate));
  assert(storage.writes == 0 && validator.calls == 0);
  storage.failRead = true;
  LocalTlsIdentity unavailable(storage, validator);
  assert(!unavailable.IsHealthy());
  assert(!Provision(unavailable, gate));
}

void TestMaximumRecordAndRotation() {
  MemoryStorage storage;
  Validator validator;
  Gate gate;
  gate.service = true;
  gate.disconnected = true;
  LocalTlsIdentity identity(storage, validator);
  std::vector<uint8_t> certificate(
      LocalTlsIdentity::MaximumCertificatePemSize, 'C');
  std::vector<uint8_t> key(LocalTlsIdentity::MaximumPrivateKeyPemSize, 'K');
  constexpr char Prefix[] = "-----BEGIN ";
  std::copy(Prefix, Prefix + sizeof(Prefix) - 1, certificate.begin());
  std::copy(Prefix, Prefix + sizeof(Prefix) - 1, key.begin());
  certificate.back() = 0;
  key.back() = 0;
  assert(identity.Provision(certificate.data(), certificate.size(),
                            key.data(), key.size(), gate));
  assert(storage.bytes.size() == LocalTlsIdentity::MaximumRecordSize);
  key[50] = 'Z';
  assert(identity.Provision(certificate.data(), certificate.size(),
                            key.data(), key.size(), gate));
  assert(storage.writes == 2 && identity.PrivateKey()[50] == 'Z');
  key.push_back(0);
  assert(!identity.Provision(certificate.data(), certificate.size(),
                             key.data(), key.size(), gate));
  assert(storage.writes == 2);
}

}  // namespace

int main() {
  TestServiceGateAndRestart();
  TestFirstInitializationPreservesRecords();
  TestMalformedRecordsAndFailedWrite();
  TestInputBoundsAndMissingStorage();
  TestMaximumRecordAndRotation();
}

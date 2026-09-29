#include <assert.h>

#include <algorithm>
#include <array>
#include <string>

#include "../src/features/provisioning/DeviceIdentity.h"

namespace {

class MemoryProvisioningStorage final : public ProvisioningStorage {
 public:
  ProvisioningReadResult Read(uint8_t* output, size_t capacity,
                              size_t& length) override {
    if (failRead) {
      return ProvisioningReadResult::Failed;
    }
    if (!present) {
      return ProvisioningReadResult::Missing;
    }
    if (storedLength > capacity) {
      return ProvisioningReadResult::Failed;
    }

    std::copy(data.begin(), data.begin() + storedLength, output);
    length = storedLength;
    return ProvisioningReadResult::Found;
  }

  bool Write(const uint8_t* input, size_t length) override {
    ++writeCount;
    if (failWrite) {
      return false;
    }
    std::copy(input, input + length, data.begin());
    storedLength = length;
    present = true;
    return true;
  }

  std::array<uint8_t, DeviceIdentity::RecordSize> data{};
  size_t storedLength = 0;
  int writeCount = 0;
  bool present = false;
  bool failRead = false;
  bool failWrite = false;
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

std::array<uint8_t, DeviceIdentity::CredentialSize> Credential() {
  std::array<uint8_t, DeviceIdentity::CredentialSize> credential{};
  for (size_t index = 0; index < credential.size(); ++index) {
    credential[index] = static_cast<uint8_t>(index + 1);
  }
  return credential;
}

void TestPhysicalGateAndRestart() {
  MemoryProvisioningStorage storage;
  DeviceIdentity initial(storage);
  FakeServiceGate gate;
  assert(initial.IsHealthy());
  assert(!initial.IsProvisioned());
  assert(!initial.Provision("kufr-001", Credential(), gate));
  gate.serviceMode = true;
  assert(!initial.Provision("kufr-001", Credential(), gate));
  assert(storage.writeCount == 0);

  gate.supplyDisconnected = true;
  assert(initial.Provision("kufr-001", Credential(), gate));
  assert(storage.writeCount == 1);
  assert(initial.IsProvisioned());
  assert(initial.DeviceId() == "kufr-001");

  DeviceIdentity restarted(storage);
  assert(restarted.IsProvisioned());
  assert(restarted.DeviceId() == "kufr-001");
  char hex[DeviceIdentity::CredentialSize * 2 + 1] = {};
  assert(restarted.CopyCredentialHex(hex, sizeof(hex)));
  assert(std::string(hex) ==
         "0102030405060708090a0b0c0d0e0f10"
         "1112131415161718191a1b1c1d1e1f20");
}

void TestMalformedIdentityAndStorageFailures() {
  MemoryProvisioningStorage storage;
  FakeServiceGate gate;
  gate.serviceMode = true;
  gate.supplyDisconnected = true;

  DeviceIdentity initial(storage);
  const std::array<uint8_t, DeviceIdentity::CredentialSize> zero{};
  assert(!initial.Provision("kufr-001", zero, gate));
  assert(!initial.Provision("bad id", Credential(), gate));
  assert(storage.writeCount == 0);

  storage.failWrite = true;
  assert(!initial.Provision("kufr-001", Credential(), gate));
  assert(!initial.IsHealthy());
  assert(!initial.IsProvisioned());

  storage.failWrite = false;
  DeviceIdentity valid(storage);
  assert(valid.Provision("kufr-001", Credential(), gate));
  storage.data[4] = 2;
  DeviceIdentity badVersion(storage);
  assert(!badVersion.IsHealthy());
  assert(!badVersion.IsProvisioned());
  char hex[DeviceIdentity::CredentialSize * 2 + 1] = {};
  assert(!badVersion.CopyCredentialHex(hex, sizeof(hex)));

  storage.data[4] = 1;
  storage.failRead = true;
  DeviceIdentity failedRead(storage);
  assert(!failedRead.IsHealthy());
}

}  // namespace

int main() {
  TestPhysicalGateAndRestart();
  TestMalformedIdentityAndStorageFailures();
  return 0;
}

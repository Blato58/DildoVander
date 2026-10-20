#include "DeviceIdentity.h"

#include <algorithm>

namespace {

constexpr uint8_t Magic[] = {'K', 'I', 'D', '1'};
constexpr uint8_t Version = 1;
constexpr size_t DeviceIdOffset = 6;
constexpr size_t CredentialOffset =
    DeviceIdOffset + DeviceIdentity::MaximumDeviceIdLength;
constexpr char HexDigits[] = "0123456789abcdef";

class RecordWiper {
 public:
  explicit RecordWiper(std::array<uint8_t, DeviceIdentity::RecordSize>& record)
      : record_(record) {}

  ~RecordWiper() {
    volatile uint8_t* bytes = record_.data();
    for (size_t index = 0; index < record_.size(); ++index) {
      bytes[index] = 0;
    }
  }

 private:
  std::array<uint8_t, DeviceIdentity::RecordSize>& record_;
};

}  // namespace

DeviceIdentity::DeviceIdentity(ProvisioningStorage& storage) : storage_(storage) {
  std::array<uint8_t, RecordSize> record{};
  RecordWiper wipe(record);
  size_t length = 0;
  const ProvisioningReadResult result = storage_.Read(record.data(),
                                                       record.size(), length);
  if (result == ProvisioningReadResult::Missing) {
    healthy_ = true;
    return;
  }
  if (result != ProvisioningReadResult::Found || length != RecordSize ||
      !std::equal(Magic, Magic + sizeof(Magic), record.begin()) ||
      record[4] != Version || record[5] == 0 ||
      record[5] > MaximumDeviceIdLength) {
    return;
  }

  std::string candidate(reinterpret_cast<const char*>(record.data() +
                                               DeviceIdOffset),
                        record[5]);
  if (!IsValidDeviceId(candidate)) {
    return;
  }
  for (size_t index = DeviceIdOffset + record[5];
       index < CredentialOffset; ++index) {
    if (record[index] != 0) {
      return;
    }
  }
  if (std::all_of(record.begin() + CredentialOffset, record.end(),
                  [](uint8_t value) { return value == 0; })) {
    return;
  }

  deviceId_ = candidate;
  std::copy(record.begin() + CredentialOffset, record.end(),
            credential_.begin());
  healthy_ = true;
  provisioned_ = true;
}

DeviceIdentity::~DeviceIdentity() { ClearCredential(); }

bool DeviceIdentity::IsHealthy() const { return healthy_; }

bool DeviceIdentity::IsProvisioned() const {
  return healthy_ && provisioned_;
}

const std::string& DeviceIdentity::DeviceId() const { return deviceId_; }

bool DeviceIdentity::CopyCredentialHex(char* output, size_t capacity) const {
  if (output != nullptr && capacity > 0) {
    output[0] = '\0';
  }
  if (!IsProvisioned() || output == nullptr ||
      capacity < CredentialSize * 2 + 1) {
    return false;
  }

  for (size_t index = 0; index < credential_.size(); ++index) {
    output[index * 2] = HexDigits[credential_[index] >> 4];
    output[index * 2 + 1] = HexDigits[credential_[index] & 0x0f];
  }
  output[CredentialSize * 2] = '\0';
  return true;
}

bool DeviceIdentity::Provision(
    const std::string& deviceId,
    const std::array<uint8_t, CredentialSize>& credential,
    const ServiceGate& gate) {
  if (!healthy_ || !gate.ServiceModeActive() ||
      !gate.ActuatorSupplyDisconnected() ||
      !IsValidDeviceId(deviceId) ||
      std::all_of(credential.begin(), credential.end(),
                  [](uint8_t value) { return value == 0; })) {
    return false;
  }

  std::array<uint8_t, RecordSize> record{};
  RecordWiper wipe(record);
  std::copy(Magic, Magic + sizeof(Magic), record.begin());
  record[4] = Version;
  record[5] = static_cast<uint8_t>(deviceId.size());
  std::copy(deviceId.begin(), deviceId.end(),
            record.begin() + DeviceIdOffset);
  std::copy(credential.begin(), credential.end(),
            record.begin() + CredentialOffset);

  const bool written = storage_.Write(record.data(), record.size());
  if (!written) {
    ClearCredential();
    deviceId_.clear();
    provisioned_ = false;
    healthy_ = false;
    return false;
  }

  ClearCredential();
  deviceId_ = deviceId;
  credential_ = credential;
  provisioned_ = true;
  return true;
}

bool DeviceIdentity::IsValidDeviceId(const std::string& deviceId) {
  if (deviceId.empty() || deviceId.size() > MaximumDeviceIdLength) {
    return false;
  }
  for (char value : deviceId) {
    if (!((value >= 'a' && value <= 'z') ||
          (value >= 'A' && value <= 'Z') ||
          (value >= '0' && value <= '9') || value == '-' || value == '_')) {
      return false;
    }
  }
  return true;
}

void DeviceIdentity::ClearCredential() {
  volatile uint8_t* bytes = credential_.data();
  for (size_t index = 0; index < credential_.size(); ++index) {
    bytes[index] = 0;
  }
}

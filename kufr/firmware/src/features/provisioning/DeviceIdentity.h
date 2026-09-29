#pragma once

#include <stdint.h>

#include <array>
#include <string>

#include "ProvisioningStorage.h"

class ServiceGate {
 public:
  virtual ~ServiceGate() = default;
  virtual bool ServiceModeActive() const = 0;
  virtual bool ActuatorSupplyDisconnected() const = 0;
};

class DeviceIdentity final {
 public:
  static constexpr size_t CredentialSize = 32;
  static constexpr size_t MaximumDeviceIdLength = 64;
  static constexpr size_t RecordSize = 102;

  explicit DeviceIdentity(ProvisioningStorage& storage);
  ~DeviceIdentity();
  DeviceIdentity(const DeviceIdentity&) = delete;
  DeviceIdentity& operator=(const DeviceIdentity&) = delete;

  bool IsHealthy() const;
  bool IsProvisioned() const;
  const std::string& DeviceId() const;
  bool CopyCredentialHex(char* output, size_t capacity) const;
  bool Provision(const std::string& deviceId,
                 const std::array<uint8_t, CredentialSize>& credential,
                 const ServiceGate& gate);

 private:
  static bool IsValidDeviceId(const std::string& deviceId);
  void ClearCredential();

  ProvisioningStorage& storage_;
  std::string deviceId_;
  std::array<uint8_t, CredentialSize> credential_{};
  bool healthy_ = false;
  bool provisioned_ = false;
};

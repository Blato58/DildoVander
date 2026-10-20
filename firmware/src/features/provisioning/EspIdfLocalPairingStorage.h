#pragma once

#include "nvs.h"

#include "LocalPairingCredential.h"

class EspIdfLocalPairingStorage final : public LocalPairingStorage {
 public:
  explicit EspIdfLocalPairingStorage(bool nvsInitialized);
  ~EspIdfLocalPairingStorage() override;

  ProvisioningReadResult Read(uint8_t* data, size_t capacity,
                              size_t& length) override;
  bool Write(const uint8_t* data, size_t length) override;

 private:
  nvs_handle_t handle_ = 0;
  bool opened_ = false;
};

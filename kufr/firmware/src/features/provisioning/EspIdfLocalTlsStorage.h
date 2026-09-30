#pragma once

#include "nvs.h"

#include "ProvisioningStorage.h"

class EspIdfLocalTlsStorage final : public ProvisioningStorage {
 public:
  explicit EspIdfLocalTlsStorage(bool nvsInitialized);
  ~EspIdfLocalTlsStorage();
  EspIdfLocalTlsStorage(const EspIdfLocalTlsStorage&) = delete;
  EspIdfLocalTlsStorage& operator=(const EspIdfLocalTlsStorage&) = delete;

  ProvisioningReadResult Read(uint8_t* data, size_t capacity,
                              size_t& length) override;
  bool Write(const uint8_t* data, size_t length) override;

 private:
  nvs_handle_t handle_ = 0;
  bool opened_ = false;
};

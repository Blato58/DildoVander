#pragma once

#include "nvs.h"

#include "TrustedTime.h"

class EspIdfTrustedTimeStore final : public LastTrustedTimeStore {
 public:
  explicit EspIdfTrustedTimeStore(bool nvsInitialized);
  ~EspIdfTrustedTimeStore() override;

  LastTrustedTimeRead Read(uint64_t& epochSeconds) override;
  bool Write(uint64_t epochSeconds) override;

 private:
  nvs_handle_t handle_ = 0;
  bool opened_ = false;
};

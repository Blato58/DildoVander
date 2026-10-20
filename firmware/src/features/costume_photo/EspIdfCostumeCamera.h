#pragma once
#include "CostumePhoto.h"

class EspIdfCostumeCamera final : public CostumeCameraPort {
 public:
  void Configure(bool nvsReady, bool profileBVerified);
  bool Available() const override { return verified_; }
  bool Capture(std::vector<uint8_t>& jpeg) override;
  bool Capture(std::vector<uint8_t>& jpeg, const std::function<bool()>& current) override;
 private:
  bool verified_ = false;
};

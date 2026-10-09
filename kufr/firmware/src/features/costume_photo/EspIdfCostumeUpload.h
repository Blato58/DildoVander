#pragma once
#include "CostumePhoto.h"
#include "../backend_access/DeviceTokenSession.h"

class EspIdfCostumeUpload final : public CostumeUploadPort {
 public:
  EspIdfCostumeUpload(const std::string& host, DeviceTokenSession& tokens,
      const TurnCorrelation& correlation, const std::string& catalog,
      const std::string& idempotencyKey, const std::function<bool()>& current);
  bool Upload(const std::vector<uint8_t>& jpeg, std::string& operationId) override;
  PhotoPoll Poll(const std::string& operationId) override;
 private:
  int Request(const std::string& path, const std::vector<uint8_t>* jpeg, std::string& response);
  std::string baseUrl_, catalog_, key_;
  DeviceTokenSession& tokens_;
  TurnCorrelation correlation_;
  std::function<bool()> current_;
};

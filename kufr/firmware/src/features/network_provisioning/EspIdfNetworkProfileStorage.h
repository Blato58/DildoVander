#pragma once
#include "NetworkProfiles.h"

class EspIdfNetworkProfileStorage final : public NetworkProfileStorage {
 public:
  bool Load(NetworkProfileRecord& record, bool& missing);
  bool Save(const NetworkProfileRecord& record) override;
};

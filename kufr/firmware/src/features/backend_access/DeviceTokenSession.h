#pragma once

#include <stdint.h>

#include <string>

#include "TokenExchangePort.h"
#include "../provisioning/DeviceIdentity.h"

class DeviceTokenSession final {
 public:
  DeviceTokenSession(DeviceIdentity& identity, TokenExchangePort& transport);
  ~DeviceTokenSession();
  DeviceTokenSession(const DeviceTokenSession&) = delete;
  DeviceTokenSession& operator=(const DeviceTokenSession&) = delete;

  bool EnsureToken(uint64_t nowMs);
  bool HandleUnauthorized(uint64_t nowMs);
  bool HasToken(uint64_t nowMs) const;
  const char* AccessToken(uint64_t nowMs) const;
  bool IsRevoked() const;

 private:
  bool RequestToken(uint64_t nowMs, bool afterUnauthorized);
  void ClearToken();
  static bool IsValidToken(const TokenExchangeResponse& response,
                           const std::string& deviceId);

  DeviceIdentity& identity_;
  TokenExchangePort& transport_;
  std::string token_;
  uint64_t expiresAtMs_ = 0;
  uint64_t renewAtMs_ = 0;
  bool retriedUnauthorized_ = false;
  bool revoked_ = false;
};

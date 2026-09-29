#pragma once

#include <stdint.h>

#include <string>

enum class TokenExchangeStatus {
  Success,
  Unauthorized,
  Failed,
};

struct TokenExchangeResponse {
  TokenExchangeStatus status = TokenExchangeStatus::Failed;
  std::string accessToken;
  std::string tokenType;
  std::string deviceId;
  uint32_t expiresInSeconds = 0;
};

class TokenExchangePort {
 public:
  virtual ~TokenExchangePort() = default;
  virtual TokenExchangeResponse Exchange(const char* credentialHex) = 0;
};

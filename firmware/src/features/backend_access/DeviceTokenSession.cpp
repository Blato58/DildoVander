#include "DeviceTokenSession.h"

#include <algorithm>
#include <array>
#include <limits>

namespace {

constexpr uint64_t RenewalPeriodMs = 12u * 60u * 1000u;
constexpr uint64_t RenewalMarginMs = 60u * 1000u;
constexpr uint32_t MaximumTokenLifetimeSeconds = 900;

void Wipe(std::string& value) {
  if (!value.empty()) {
    volatile char* bytes = &value[0];
    for (size_t index = 0; index < value.size(); ++index) {
      bytes[index] = 0;
    }
    value.clear();
  }
}

class CredentialBuffer final {
 public:
  ~CredentialBuffer() {
    volatile char* bytes = value.data();
    for (size_t index = 0; index < value.size(); ++index) {
      bytes[index] = 0;
    }
  }

  char* Data() { return value.data(); }
  size_t Size() const { return value.size(); }

 private:
  std::array<char, DeviceIdentity::CredentialSize * 2 + 1> value{};
};

}  // namespace

DeviceTokenSession::DeviceTokenSession(DeviceIdentity& identity,
                                       TokenExchangePort& transport)
    : identity_(identity), transport_(transport) {}

DeviceTokenSession::~DeviceTokenSession() { ClearToken(); }

bool DeviceTokenSession::EnsureToken(uint64_t nowMs) {
  if (revoked_ || !identity_.IsProvisioned()) {
    ClearToken();
    return false;
  }
  if (HasToken(nowMs) && nowMs < renewAtMs_) {
    return true;
  }
  return RequestToken(nowMs, false) || HasToken(nowMs);
}

bool DeviceTokenSession::HandleUnauthorized(uint64_t nowMs) {
  if (revoked_ || retriedUnauthorized_ || !identity_.IsProvisioned()) {
    ClearToken();
    return false;
  }
  ClearToken();
  retriedUnauthorized_ = true;
  return RequestToken(nowMs, true);
}

bool DeviceTokenSession::HasToken(uint64_t nowMs) const {
  return !token_.empty() && nowMs < expiresAtMs_;
}

const char* DeviceTokenSession::AccessToken(uint64_t nowMs) const {
  return HasToken(nowMs) ? token_.c_str() : nullptr;
}

bool DeviceTokenSession::IsRevoked() const { return revoked_; }

bool DeviceTokenSession::RequestToken(uint64_t nowMs,
                                      bool afterUnauthorized) {
  CredentialBuffer credential;
  if (!identity_.CopyCredentialHex(credential.Data(), credential.Size())) {
    ClearToken();
    return false;
  }

  TokenExchangeResponse response = transport_.Exchange(credential.Data());
  if (response.status == TokenExchangeStatus::Unauthorized) {
    revoked_ = true;
    ClearToken();
    Wipe(response.accessToken);
    return false;
  }
  if (response.status != TokenExchangeStatus::Success ||
      !IsValidToken(response, identity_.DeviceId()) ||
      nowMs > std::numeric_limits<uint64_t>::max() -
                  response.expiresInSeconds * 1000u) {
    Wipe(response.accessToken);
    return false;
  }

  const uint64_t lifetimeMs = response.expiresInSeconds * 1000u;
  const uint64_t renewalDelayMs =
      lifetimeMs > RenewalMarginMs
          ? std::min(RenewalPeriodMs, lifetimeMs - RenewalMarginMs)
          : lifetimeMs / 2;
  ClearToken();
  token_ = response.accessToken;
  Wipe(response.accessToken);
  expiresAtMs_ = nowMs + lifetimeMs;
  renewAtMs_ = nowMs + renewalDelayMs;
  retriedUnauthorized_ = afterUnauthorized;
  return true;
}

void DeviceTokenSession::ClearToken() {
  Wipe(token_);
  expiresAtMs_ = 0;
  renewAtMs_ = 0;
}

bool DeviceTokenSession::IsValidToken(
    const TokenExchangeResponse& response, const std::string& deviceId) {
  if (response.deviceId != deviceId || response.tokenType != "Bearer" ||
      response.expiresInSeconds == 0 ||
      response.expiresInSeconds > MaximumTokenLifetimeSeconds ||
      response.accessToken.empty() || response.accessToken.size() > 256) {
    return false;
  }
  for (char value : response.accessToken) {
    if (!((value >= 'A' && value <= 'Z') ||
          (value >= 'a' && value <= 'z') ||
          (value >= '0' && value <= '9') ||
          value == '+' || value == '/' || value == '=')) {
      return false;
    }
  }
  return true;
}

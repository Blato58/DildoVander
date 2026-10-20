#include <assert.h>

#include <algorithm>
#include <array>
#include <deque>
#include <string>

#include "../src/features/backend_access/DeviceTokenSession.h"
#include "../src/features/backend_access/TokenResponseParser.h"

namespace {

class MemoryStorage final : public ProvisioningStorage {
 public:
  ProvisioningReadResult Read(uint8_t*, size_t, size_t&) override {
    return ProvisioningReadResult::Missing;
  }
  bool Write(const uint8_t*, size_t) override { return true; }
};

class EnabledGate final : public ServiceGate {
 public:
  bool ServiceModeActive() const override { return true; }
  bool ActuatorSupplyDisconnected() const override { return true; }
};

class FakeTransport final : public TokenExchangePort {
 public:
  TokenExchangeResponse Exchange(const char* credentialHex) override {
    ++calls;
    assert(std::string(credentialHex) ==
           "0102030405060708090a0b0c0d0e0f10"
           "1112131415161718191a1b1c1d1e1f20");
    assert(!responses.empty());
    TokenExchangeResponse response = responses.front();
    responses.pop_front();
    return response;
  }

  std::deque<TokenExchangeResponse> responses;
  int calls = 0;
};

std::array<uint8_t, DeviceIdentity::CredentialSize> Credential() {
  std::array<uint8_t, DeviceIdentity::CredentialSize> credential{};
  for (size_t index = 0; index < credential.size(); ++index) {
    credential[index] = static_cast<uint8_t>(index + 1);
  }
  return credential;
}

TokenExchangeResponse Success(const std::string& token = "TokenA") {
  TokenExchangeResponse response;
  response.status = TokenExchangeStatus::Success;
  response.accessToken = token;
  response.tokenType = "Bearer";
  response.deviceId = "kufr-001";
  response.expiresInSeconds = 900;
  return response;
}

void TestScheduledRenewalAndExpiredToken() {
  MemoryStorage storage;
  DeviceIdentity identity(storage);
  EnabledGate gate;
  assert(identity.Provision("kufr-001", Credential(), gate));
  FakeTransport transport;
  transport.responses.push_back(Success("TokenA"));
  transport.responses.push_back({});
  transport.responses.push_back({});
  DeviceTokenSession session(identity, transport);

  assert(session.EnsureToken(0));
  assert(std::string(session.AccessToken(0)) == "TokenA");
  assert(session.EnsureToken(719999));
  assert(transport.calls == 1);
  assert(session.EnsureToken(720000));
  assert(transport.calls == 2);
  assert(session.HasToken(899999));
  assert(!session.EnsureToken(900000));
  assert(session.AccessToken(900000) == nullptr);
  assert(transport.calls == 3);
}

void TestOneReauthenticationAfterUnauthorized() {
  MemoryStorage storage;
  DeviceIdentity identity(storage);
  EnabledGate gate;
  assert(identity.Provision("kufr-001", Credential(), gate));
  FakeTransport transport;
  transport.responses.push_back(Success("TokenA"));
  transport.responses.push_back(Success("TokenB"));
  DeviceTokenSession session(identity, transport);

  assert(session.EnsureToken(0));
  assert(session.HandleUnauthorized(100));
  assert(std::string(session.AccessToken(100)) == "TokenB");
  assert(!session.HandleUnauthorized(200));
  assert(session.AccessToken(200) == nullptr);
  assert(transport.calls == 2);
}

void TestRevocationAndInvalidResponseStayOffline() {
  MemoryStorage storage;
  DeviceIdentity identity(storage);
  EnabledGate gate;
  assert(identity.Provision("kufr-001", Credential(), gate));
  FakeTransport transport;
  TokenExchangeResponse wrongDevice = Success();
  wrongDevice.deviceId = "other-device";
  transport.responses.push_back(wrongDevice);
  transport.responses.push_back({TokenExchangeStatus::Unauthorized});
  DeviceTokenSession session(identity, transport);

  assert(!session.EnsureToken(0));
  assert(!session.IsRevoked());
  assert(!session.EnsureToken(100));
  assert(session.IsRevoked());
  assert(!session.EnsureToken(200));
  assert(transport.calls == 2);
}

void TestMissingIdentityNeverContactsTransport() {
  MemoryStorage storage;
  DeviceIdentity identity(storage);
  FakeTransport transport;
  DeviceTokenSession session(identity, transport);
  assert(!session.EnsureToken(0));
  assert(transport.calls == 0);
}

void TestBoundedTokenResponseParser() {
  constexpr char valid[] =
      "{\"deviceId\":\"kufr-001\",\"expiresInSeconds\":900,"
      "\"tokenType\":\"Bearer\",\"accessToken\":\"Abc+/=\"}";
  TokenExchangeResponse response;
  assert(ParseTokenResponse(valid, sizeof(valid) - 1, response));
  assert(response.status == TokenExchangeStatus::Success);
  assert(response.accessToken == "Abc+/=");

  constexpr char duplicate[] =
      "{\"deviceId\":\"kufr-001\",\"deviceId\":\"other\","
      "\"expiresInSeconds\":900,\"tokenType\":\"Bearer\","
      "\"accessToken\":\"Abc\"}";
  assert(!ParseTokenResponse(duplicate, sizeof(duplicate) - 1, response));
  assert(response.status == TokenExchangeStatus::Failed);
  assert(response.accessToken.empty());

  constexpr char escaped[] =
      "{\"deviceId\":\"kufr-001\",\"expiresInSeconds\":900,"
      "\"tokenType\":\"Bearer\",\"accessToken\":\"Abc\\u003d\"}";
  assert(!ParseTokenResponse(escaped, sizeof(escaped) - 1, response));

  constexpr char fractional[] =
      "{\"deviceId\":\"kufr-001\",\"expiresInSeconds\":900.5,"
      "\"tokenType\":\"Bearer\",\"accessToken\":\"Abc\"}";
  assert(!ParseTokenResponse(fractional, sizeof(fractional) - 1, response));
}

}  // namespace

int main() {
  TestScheduledRenewalAndExpiredToken();
  TestOneReauthenticationAfterUnauthorized();
  TestRevocationAndInvalidResponseStayOffline();
  TestMissingIdentityNeverContactsTransport();
  TestBoundedTokenResponseParser();
  return 0;
}

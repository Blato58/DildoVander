#include <cassert>

#include <array>
#include <deque>
#include <string>

#include "../src/features/conversation/ConversationStartCoordinator.h"

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

class FakeTokenTransport final : public TokenExchangePort {
 public:
  TokenExchangeResponse Exchange(const char*) override {
    assert(!responses.empty());
    TokenExchangeResponse result = responses.front();
    responses.pop_front();
    return result;
  }
  std::deque<TokenExchangeResponse> responses;
};

class FakeConversationTransport final : public ConversationStartPort {
 public:
  ConversationStartHttpResult Create(const ConversationStartRequest& request,
                                     const char* token) override {
    assert(!responses.empty());
    keys.push_back(request.idempotencyKey);
    tokens.push_back(token);
    ConversationStartHttpResult result = responses.front();
    responses.pop_front();
    return result;
  }
  std::deque<ConversationStartHttpResult> responses;
  std::deque<std::string> keys;
  std::deque<std::string> tokens;
};

TokenExchangeResponse Token(const std::string& value) {
  TokenExchangeResponse response;
  response.status = TokenExchangeStatus::Success;
  response.accessToken = value;
  response.tokenType = "Bearer";
  response.deviceId = "kufr-001";
  response.expiresInSeconds = 900;
  return response;
}

std::array<uint8_t, DeviceIdentity::CredentialSize> Credential() {
  std::array<uint8_t, DeviceIdentity::CredentialSize> bytes{};
  bytes[0] = 1;
  return bytes;
}

ConversationStartRequest Request() {
  ConversationStartRequest request;
  request.deviceId = "kufr-001";
  request.bootId = "c85743d5-1b72-41b8-8500-ae3c102b27d6";
  request.idempotencyKey = "7731ce10-a53c-4934-8909-06870fc86a12";
  request.catalogVersion = "demo-v1";
  return request;
}

ConversationStartHttpResult Started() {
  ConversationStartHttpResult result;
  result.statusCode = 201;
  result.conversation.conversationId =
      "9a8dd264-61f7-4339-b677-866798263cb2";
  return result;
}

void TestOneRefreshAndStableIdempotency() {
  MemoryStorage storage;
  DeviceIdentity identity(storage);
  EnabledGate gate;
  assert(identity.Provision("kufr-001", Credential(), gate));
  FakeTokenTransport tokenTransport;
  tokenTransport.responses.push_back(Token("TokenA"));
  tokenTransport.responses.push_back(Token("TokenB"));
  DeviceTokenSession tokens(identity, tokenTransport);
  FakeConversationTransport conversationTransport;
  conversationTransport.responses.push_back({401, {}});
  conversationTransport.responses.push_back(Started());
  ConversationStartCoordinator coordinator(identity, tokens,
                                            conversationTransport);

  ConversationStartRequest request = Request();
  assert(coordinator.Start(request, 0) == ConversationStartOutcome::Started);
  assert(coordinator.ConversationId() ==
         "9a8dd264-61f7-4339-b677-866798263cb2");
  assert(conversationTransport.keys.size() == 2);
  assert(conversationTransport.keys[0] == conversationTransport.keys[1]);
  assert(conversationTransport.tokens[0] == "TokenA");
  assert(conversationTransport.tokens[1] == "TokenB");
  assert(coordinator.Start(request, 1) ==
         ConversationStartOutcome::AlreadyStarted);
}

void TestInvalidScopeAndRejectedServerResult() {
  MemoryStorage storage;
  DeviceIdentity identity(storage);
  EnabledGate gate;
  assert(identity.Provision("kufr-001", Credential(), gate));
  FakeTokenTransport tokenTransport;
  tokenTransport.responses.push_back(Token("TokenA"));
  DeviceTokenSession tokens(identity, tokenTransport);
  FakeConversationTransport conversationTransport;
  conversationTransport.responses.push_back({409, {}});
  ConversationStartCoordinator coordinator(identity, tokens,
                                            conversationTransport);

  ConversationStartRequest request = Request();
  request.deviceId = "kufr-other";
  assert(coordinator.Start(request, 0) == ConversationStartOutcome::Rejected);
  assert(conversationTransport.keys.empty());
  request = Request();
  assert(coordinator.Start(request, 0) == ConversationStartOutcome::Rejected);
  assert(coordinator.ConversationId().empty());
}

void TestMalformedSuccessStaysOffline() {
  MemoryStorage storage;
  DeviceIdentity identity(storage);
  EnabledGate gate;
  assert(identity.Provision("kufr-001", Credential(), gate));
  FakeTokenTransport tokenTransport;
  tokenTransport.responses.push_back(Token("TokenA"));
  DeviceTokenSession tokens(identity, tokenTransport);
  FakeConversationTransport conversationTransport;
  conversationTransport.responses.push_back({201, {}});
  ConversationStartCoordinator coordinator(identity, tokens,
                                            conversationTransport);

  assert(coordinator.Start(Request(), 0) == ConversationStartOutcome::Offline);
  assert(coordinator.ConversationId().empty());
}

void TestRetryAfterTransportFailureReusesRequestId() {
  MemoryStorage storage;
  DeviceIdentity identity(storage);
  EnabledGate gate;
  assert(identity.Provision("kufr-001", Credential(), gate));
  FakeTokenTransport tokenTransport;
  tokenTransport.responses.push_back(Token("TokenA"));
  DeviceTokenSession tokens(identity, tokenTransport);
  FakeConversationTransport conversationTransport;
  conversationTransport.responses.push_back({});
  conversationTransport.responses.push_back(Started());
  ConversationStartCoordinator coordinator(identity, tokens,
                                            conversationTransport);

  const ConversationStartRequest request = Request();
  assert(coordinator.Start(request, 0) == ConversationStartOutcome::Offline);
  assert(coordinator.Start(request, 1) == ConversationStartOutcome::Started);
  assert(conversationTransport.keys.size() == 2);
  assert(conversationTransport.keys[0] == conversationTransport.keys[1]);
}

}  // namespace

int main() {
  TestOneRefreshAndStableIdempotency();
  TestInvalidScopeAndRejectedServerResult();
  TestMalformedSuccessStaysOffline();
  TestRetryAfterTransportFailureReusesRequestId();
}

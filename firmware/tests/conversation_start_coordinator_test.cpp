#include <cassert>

#include <array>
#include <deque>
#include <string>

#include "../src/features/conversation/ConversationStartCoordinator.h"
#include "../src/features/backend_access/CustomerSessionRecovery.h"

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
  MemoryStorage storage;
  DeviceIdentity identity(storage);
  FakeTokenTransport tokenTransport;
  DeviceTokenSession tokens(identity, tokenTransport);
  FakeConversationTransport transport;
  ConversationStartCoordinator customer(identity, tokens, transport);
  assert(!customer.AdoptCustomerConversation("untrusted"));
  const std::string accepted = "12345678-1234-4234-8234-123456789abc";
  assert(customer.AdoptCustomerConversation(accepted));
  assert(customer.ConversationId() == accepted);
  assert(!customer.AdoptCustomerConversation("22345678-1234-4234-8234-123456789abc"));
  assert(customer.Start(Request(), 0) == ConversationStartOutcome::AlreadyStarted);
  assert(transport.keys.empty());
  assert(CanRetireUnknownCustomerStart("recovering", "boot-A", "op-A", false, "boot-A", "op-A"));
  assert(!CanRetireUnknownCustomerStart("recovering", "boot-A", "op-A", true, "boot-A", "op-A"));
  assert(!CanRetireUnknownCustomerStart("recovering", "boot-A", "op-A", false, "boot-B", "op-A"));
  assert(!CanRetireUnknownCustomerStart("recovering", "boot-A", "op-A", false, "boot-A", "op-B"));
  assert(!CanRetireUnknownCustomerStart("running", "boot-A", "op-A", false, "boot-A", "op-A"));
  // The same freshness predicate gates cache reuse, BLE disclosure and new
  // queue admission. A runtime transition invalidates even a young challenge.
  assert(IsCustomerChallengeCurrent(true, false, true, 2000, 1000, 60000));
  assert(!IsCustomerChallengeCurrent(true, false, false, 2000, 1000, 60000));
  assert(!IsCustomerChallengeCurrent(true, false, false, 2000, 1000, 90000));
  assert(!IsCustomerChallengeCurrent(true, true, true, 2000, 1000, 90000));
  assert(!IsCustomerChallengeCurrent(false, false, true, 2000, 1000, 90000));
  assert(!IsCustomerChallengeCurrent(true, false, true, 61000, 1000, 60000));
  assert(!IsCustomerChallengeCurrent(true, false, true, 999, 1000, 90000));
  // A successful replacement bound to the new snapshot restores service
  // immediately, rather than waiting for the previous 60-second cache age.
  assert(IsCustomerChallengeCurrent(true, false, true, 2001, 2001, 60000));
  std::string order;
  assert(ReconcileCustomerBoot(true, [&]() { order += "register;"; return true; },
      [&]() { order += "idle;"; return true; }));
  assert(order == "register;idle;");
  order.clear();
  assert(!ReconcileCustomerBoot(true, [&]() { order += "register;"; return false; },
      [&]() { order += "idle;"; return true; }));
  assert(order == "register;");
  order.clear();
  assert(!ReconcileCustomerBoot(false, [&]() { order += "register;"; return true; },
      [&]() { order += "idle;"; return true; }));
  assert(order == "register;");
}

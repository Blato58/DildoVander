#include <cassert>
#include <string>

#include "../src/features/conversation/ConversationStartCodec.h"

namespace {

const std::string BootId = "c85743d5-1b72-41b8-8500-ae3c102b27d6";
const std::string RequestId = "7731ce10-a53c-4934-8909-06870fc86a12";
const std::string ConversationId = "9a8dd264-61f7-4339-b677-866798263cb2";

bool Parse(const std::string& json, ConversationStartResponse& response) {
  return ParseConversationStartResponse(json.data(), json.size(), response);
}

}  // namespace

int main() {
  ConversationStartRequest request;
  request.deviceId = "kufr-001";
  request.bootId = BootId;
  request.idempotencyKey = RequestId;
  request.catalogVersion = "demo-v1";
  std::string body;
  assert(BuildConversationStartRequest(request, body));
  assert(body == "{\"deviceId\":\"kufr-001\",\"bootId\":\"" + BootId +
                     "\",\"generation\":0,\"catalogVersion\":\"demo-v1\","
                     "\"locale\":\"cs-CZ\",\"consent\":{\"audio\":false,\"image\":false}}");
  std::string replay;
  assert(BuildConversationStartRequest(request, replay) && replay == body);
  request.catalogVersion = "bad\"value";
  assert(!BuildConversationStartRequest(request, body) && body.empty());
  request.catalogVersion = "demo-v1";
  request.idempotencyKey = "bad";
  assert(!BuildConversationStartRequest(request, body));

  ConversationStartResponse response;
  const std::string valid =
      "{\"conversationId\":\"" + ConversationId +
      "\",\"expiresAt\":\"2026-09-29T14:00:00Z\",\"limits\":{"
      "\"maxTurns\":10,\"maxTextCharacters\":1000,\"idleSeconds\":900,"
      "\"maxLifetimeSeconds\":1800}}";
  assert(Parse(valid, response));
  assert(response.conversationId == ConversationId);
  assert(response.expiresAt == "2026-09-29T14:00:00Z");
  assert(!Parse(valid + "{}", response));
  assert(response.conversationId.empty());
  assert(!Parse(std::string(513, 'x'), response));
  assert(!Parse(valid.substr(0, valid.size() - 1), response));
  assert(!Parse("{\"conversationId\":\"" + ConversationId +
                    "\",\"conversationId\":\"" + ConversationId +
                    "\",\"expiresAt\":\"x\",\"limits\":{"
                    "\"maxTurns\":10,\"maxTextCharacters\":1000,"
                    "\"idleSeconds\":900,\"maxLifetimeSeconds\":1800}}",
                response));
  assert(!Parse("{\"conversationId\":\"" + ConversationId +
                    "\",\"expiresAt\":\"x\",\"limits\":{"
                    "\"maxTurns\":11,\"maxTextCharacters\":1000,"
                    "\"idleSeconds\":900,\"maxLifetimeSeconds\":1800}}",
                response));
  assert(!Parse("{\"conversationId\":\"bad\\u0022\","
                    "\"expiresAt\":\"x\",\"limits\":{"
                    "\"maxTurns\":10,\"maxTextCharacters\":1000,"
                    "\"idleSeconds\":900,\"maxLifetimeSeconds\":1800}}",
                response));
}

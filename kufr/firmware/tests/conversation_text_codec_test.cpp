#include <cassert>
#include <string>

#include "../src/features/conversation/ConversationTextCodec.h"

namespace {

ConversationTextTurnRequest Request() {
  ConversationTextTurnRequest request;
  request.conversationId = "9a8dd264-61f7-4339-b677-866798263cb2";
  request.clientTurnId = "8475532e-92b2-487e-b476-7f98f0030e68";
  request.bootId = "c85743d5-1b72-41b8-8500-ae3c102b27d6";
  request.idempotencyKey = "7731ce10-a53c-4934-8909-06870fc86a12";
  request.catalogVersion = "demo-v1";
  request.text = "Ahoj";
  request.readyMask = 63;
  request.deviceRevision = 4;
  return request;
}

}  // namespace

int main() {
  ConversationTextTurnRequest request = Request();
  std::string body;
  assert(BuildConversationTextTurnRequest(request, body));
  assert(body ==
         "{\"clientTurnId\":\"8475532e-92b2-487e-b476-7f98f0030e68\","
         "\"bootId\":\"c85743d5-1b72-41b8-8500-ae3c102b27d6\","
         "\"generation\":0,\"text\":\"Ahoj\",\"readyMask\":63,"
         "\"deviceRevision\":4,\"catalogVersion\":\"demo-v1\"}");

  request.text = "Řekni \"ahoj\"\\prosím\n";
  assert(BuildConversationTextTurnRequest(request, body));
  assert(body.find("\\\"ahoj\\\"\\\\prosím\\u000a") !=
         std::string::npos);
  request.text = std::string(1001, 'a');
  assert(!BuildConversationTextTurnRequest(request, body) && body.empty());
  request.text = std::string(1000, 'a');
  assert(BuildConversationTextTurnRequest(request, body));
  request.text = std::string("bad\xc0\xaf", 5);
  assert(!BuildConversationTextTurnRequest(request, body));
  request.text = " \t\n ";
  assert(!BuildConversationTextTurnRequest(request, body));
  request = Request();
  request.readyMask = 64;
  assert(!BuildConversationTextTurnRequest(request, body));
  request = Request();
  request.clientTurnId = "not-a-uuid";
  assert(!BuildConversationTextTurnRequest(request, body));
  request = Request();
  request.catalogVersion = "bad\"version";
  assert(!BuildConversationTextTurnRequest(request, body));
}

#include "ConversationTextCodec.h"

#include "ConversationSession.h"
#include "ConversationStartCodec.h"
#include "../../protocol/JsonWriter.h"

namespace {



bool IsValidCatalogVersion(const std::string& version) {
  if (version.empty() || version.size() > 128) {
    return false;
  }
  for (char value : version) {
    if (!((value >= 'A' && value <= 'Z') ||
          (value >= 'a' && value <= 'z') ||
          (value >= '0' && value <= '9') || value == '-' ||
          value == '_' || value == '.')) {
      return false;
    }
  }
  return true;
}

bool HasNonWhitespaceText(const std::string& value) {
  for (unsigned char current : value) {
    if (current > 0x7f ||
        (current != ' ' && current != '\t' && current != '\r' &&
         current != '\n')) {
      return true;
    }
  }
  return false;
}

}  // namespace

bool BuildConversationTextTurnRequest(
    const ConversationTextTurnRequest& request, std::string& body) {
  body.clear();
  if (!IsConversationUuid(request.conversationId) ||
      !IsConversationUuid(request.clientTurnId) ||
      !IsConversationUuid(request.bootId) ||
      !IsConversationUuid(request.idempotencyKey) ||
      !IsValidCatalogVersion(request.catalogVersion) ||
      request.readyMask > 63 || request.text.size() > 4000 ||
      !IsValidConversationText(request.text) ||
      !HasNonWhitespaceText(request.text)) {
    return false;
  }

  body = "{\"clientTurnId\":\"" + request.clientTurnId +
         "\",\"bootId\":\"" + request.bootId +
         "\",\"generation\":" + std::to_string(request.generation) +
         ",\"text\":";
  AppendJsonString(request.text, body);
  body += ",\"readyMask\":" + std::to_string(request.readyMask) +
          ",\"deviceRevision\":" +
          std::to_string(request.deviceRevision) +
          ",\"catalogVersion\":\"" + request.catalogVersion + "\"";
  if (request.operationEpoch != 0)
    body += ",\"operationEpoch\":\"" + std::to_string(request.operationEpoch) + "\"";
  body += "}";
  if (body.size() > 4096) {
    body.clear();
    return false;
  }
  return true;
}

bool BuildConversationOpeningRequest(
    const ConversationTextTurnRequest& request, std::string& body) {
  body.clear();
  if (!IsConversationUuid(request.conversationId) ||
      !IsConversationUuid(request.clientTurnId) ||
      !IsConversationUuid(request.bootId) ||
      !IsConversationUuid(request.idempotencyKey) ||
      !IsValidCatalogVersion(request.catalogVersion) ||
      request.readyMask > 63 || !request.text.empty()) {
    return false;
  }
  body = "{\"clientTurnId\":\"" + request.clientTurnId +
         "\",\"bootId\":\"" + request.bootId +
         "\",\"generation\":" + std::to_string(request.generation) +
         ",\"readyMask\":" + std::to_string(request.readyMask) +
         ",\"deviceRevision\":" +
         std::to_string(request.deviceRevision) +
         ",\"catalogVersion\":\"" + request.catalogVersion + "\"";
  if (request.operationEpoch != 0)
    body += ",\"operationEpoch\":\"" + std::to_string(request.operationEpoch) + "\"";
  body += "}";
  return body.size() <= 4096;
}

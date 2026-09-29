#include "ConversationStartCoordinator.h"

ConversationStartCoordinator::ConversationStartCoordinator(
    DeviceIdentity& identity, DeviceTokenSession& tokens,
    ConversationStartPort& transport)
    : identity_(identity), tokens_(tokens), transport_(transport) {}

ConversationStartOutcome ConversationStartCoordinator::Start(
    const ConversationStartRequest& request, uint64_t nowMs) {
  if (!conversationId_.empty()) {
    return ConversationStartOutcome::AlreadyStarted;
  }
  std::string body;
  if (!identity_.IsProvisioned() ||
      request.deviceId != identity_.DeviceId() ||
      !BuildConversationStartRequest(request, body)) {
    return ConversationStartOutcome::Rejected;
  }
  if (!tokens_.EnsureToken(nowMs)) {
    return ConversationStartOutcome::Offline;
  }

  ConversationStartHttpResult response =
      transport_.Create(request, tokens_.AccessToken(nowMs));
  if (response.statusCode == 401) {
    if (!tokens_.HandleUnauthorized(nowMs)) {
      return ConversationStartOutcome::Offline;
    }
    // A retry must carry the original body and Idempotency-Key.
    response = transport_.Create(request, tokens_.AccessToken(nowMs));
  }
  if (response.statusCode == 201 &&
      !response.conversation.conversationId.empty()) {
    conversationId_ = response.conversation.conversationId;
    return ConversationStartOutcome::Started;
  }
  if (response.statusCode == 409 || response.statusCode == 429 ||
      response.statusCode == 400 || response.statusCode == 403) {
    return ConversationStartOutcome::Rejected;
  }
  return ConversationStartOutcome::Offline;
}

void ConversationStartCoordinator::Clear() { conversationId_.clear(); }

const std::string& ConversationStartCoordinator::ConversationId() const {
  return conversationId_;
}

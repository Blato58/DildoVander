#pragma once

#include <stdint.h>

#include <string>

#include "ConversationStartPort.h"
#include "../backend_access/DeviceTokenSession.h"
#include "DeviceIdentity.h"

enum class ConversationStartOutcome {
  Started,
  AlreadyStarted,
  Offline,
  Rejected,
};

// Coordinates a single start attempt; callers retain the same UUID on retry.
class ConversationStartCoordinator final {
 public:
  ConversationStartCoordinator(DeviceIdentity& identity,
                               DeviceTokenSession& tokens,
                               ConversationStartPort& transport);

  ConversationStartOutcome Start(const ConversationStartRequest& request,
                                 uint64_t nowMs);
  void Clear();
  bool AdoptCustomerConversation(const std::string& id) {
    if (!conversationId_.empty() || !IsConversationUuid(id)) return false;
    conversationId_ = id; return true;
  }
  const std::string& ConversationId() const;

 private:
  DeviceIdentity& identity_;
  DeviceTokenSession& tokens_;
  ConversationStartPort& transport_;
  std::string conversationId_;
};

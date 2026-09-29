#pragma once

#include "ConversationStartCodec.h"

struct ConversationStartHttpResult {
  int statusCode = 0;
  ConversationStartResponse conversation;
};

class ConversationStartPort {
 public:
  virtual ~ConversationStartPort() = default;
  virtual ConversationStartHttpResult Create(
      const ConversationStartRequest& request,
      const char* accessToken) = 0;
};

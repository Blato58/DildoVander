#pragma once
#include <string>
#include "../conversation/TurnOperationCodec.h"

struct RealtimeEvent {
  std::string type, streamId, inputId, operationId, delta, code;
  TurnOperationObservation operation;
};
bool ParseRealtimeEvent(const std::string& line, RealtimeEvent& event);

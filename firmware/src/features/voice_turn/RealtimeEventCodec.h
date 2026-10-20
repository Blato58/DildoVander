#pragma once
#include <cstdint>
#include <string>
#include "../conversation/TurnOperationCodec.h"

struct RealtimeEvent {
  std::string type, streamId, inputId, operationId, delta, code, playbackId;
  uint64_t pcmBytes = 0, decisionRevision = 0, responseGeneration = 0;
  bool hasResponseGeneration = false;
  uint64_t visualRevision = 0;
  bool hasVisualRevision = false;
  TurnOperationObservation operation;
};
bool ParseRealtimeEvent(const std::string& line, RealtimeEvent& event);

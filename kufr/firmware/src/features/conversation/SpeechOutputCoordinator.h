#pragma once

#include "SpeechAudioBuffer.h"

class SpeechTransportPort {
 public:
  virtual ~SpeechTransportPort() = default;
  virtual int Submit(const std::string& conversationId, const std::string& turnId,
                     const std::string& key, const char* token, std::string& operationId) = 0;
  virtual int Poll(const std::string& operationId, const char* token,
                   SpeechOperationObservation& result) = 0;
  virtual int Release(const std::string& operationId, const std::string& key, const char* token) = 0;
  virtual int Download(const std::string& operationId, const TurnCorrelation& expected,
                       const SpeechOperationObservation& result,
                       const ConversationRuntimeSnapshot& snapshot,
                       const ConversationRuntimeState& runtime, uint64_t utc,
                       const char* token, SpeechAudioBuffer& buffer) = 0;
};

class SpeechClockPort {
 public:
  virtual ~SpeechClockPort() = default;
  virtual uint64_t NowMs() = 0;
  virtual uint64_t UtcSeconds() = 0;
  virtual void WaitMs(uint32_t milliseconds) = 0;
};

enum class SpeechOutputOutcome : uint8_t { Ready, Rejected, Failed, Cancelled, Expired };

// Network-task-only orchestration, no I2S call. The source must be the completed
// text operation for this exact accepted turn, with backend audio consent.
SpeechOutputOutcome PrepareSpeechOutput(
    const std::string& sourceOperationId, const TurnCorrelation& expected,
    const TurnOperationObservation& source,
    const ConversationRuntimeSnapshot& snapshot,
    const ConversationRuntimeState& runtime, bool audioConsented,
    uint64_t turnStartedAtMs, const std::string& requestKey,
    const std::string& releaseKey, const char* token,
    SpeechTransportPort& transport, SpeechClockPort& clock,
    SpeechAudioBuffer& buffer);

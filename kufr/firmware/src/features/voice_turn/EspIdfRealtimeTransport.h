#pragma once
#include <atomic>
#include <functional>
#include <mutex>
#include "VoiceAudioPort.h"
#include "RealtimeEventCodec.h"
#include "../conversation/ConversationRuntimeState.h"

class EspIdfRealtimeTransport final {
 public:
  EspIdfRealtimeTransport(const std::string& host, const std::string& token,
                          VoiceAudioPort& audio,
                          std::function<bool()> current,
                          std::function<void(const char*)> phase, bool gptLive = false);
  ~EspIdfRealtimeTransport();
  bool Run(const std::string& body, const std::string& key,
           const TurnCorrelation& opening,
           const ConversationRuntimeSnapshot& snapshot,
           const std::function<bool(const TurnOperationObservation&)>& completed);
  const std::string& Error() const { return error_; }
 private:
  static void UploadTask(void* argument);
  void Upload();
  bool Event(const RealtimeEvent& event,
              const ConversationRuntimeSnapshot& snapshot,
              const std::function<bool(const TurnOperationObservation&)>& completed);
  int Post(const std::string& path, const void* data, size_t size,
           const char* contentType, const std::string& inputId = "", unsigned sequence = 0);
  bool Current() const;
  bool gptLive_ = false, segmentPlaying_ = false;
  std::string endpoint_ = "realtime-live";
  std::string root_, token_, conversationId_, streamId_, inputId_, operationId_, error_;
  VoiceAudioPort& audio_;
  std::function<bool()> current_;
  std::function<void(const char*)> phase_;
  TurnCorrelation expected_;
  std::mutex inputMutex_;
  std::atomic<bool> stop_{false}, uploading_{false}, uploadDone_{true}, failed_{false};
  std::atomic<uint32_t> inputEpoch_{0};
  bool sessionSeen_ = false, terminal_ = false, closed_ = false;
  unsigned sequence_ = 0, turns_ = 0;
  size_t audioBytes_ = 0, inputBytes_ = 0;
  uint64_t responseGeneration_ = 0;
  std::atomic<uint64_t> startedAtMs_{0}, lastTurnAtMs_{0};
};

#pragma once
#include <atomic>
#include <functional>
#include <memory>
#include "VoiceAudioPort.h"
#include "RealtimeSessionEngine.h"
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
  int Post(const std::string& path, const void* data, size_t size,
           const char* contentType, const std::string& inputId = "", unsigned sequence = 0);
  bool Current() const;
  bool gptLive_ = false;
  std::string endpoint_ = "realtime-live", root_, token_, error_;
  VoiceAudioPort& audio_;
  std::function<bool()> current_;
  std::function<void(const char*)> phase_;
  std::unique_ptr<RealtimeSessionEngine> engine_;
  std::atomic<bool> uploadDone_{true};
};

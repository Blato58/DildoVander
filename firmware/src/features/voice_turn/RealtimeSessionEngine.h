#pragma once
#include <atomic>
#include <functional>
#include <mutex>
#include "VoiceAudioPort.h"
#include "RealtimeEventCodec.h"
#include "../conversation/ConversationRuntimeState.h"

// Both platforms use this owner of the production event/input state machine.
// Network, clocks and media are platform ports; no platform may select a slot.
class RealtimeSessionEngine final {
 public:
  using PostPort = std::function<int(const std::string&, const void*, size_t,
      const char*, const std::string&, unsigned)>;
  RealtimeSessionEngine(VoiceAudioPort& audio, bool gptLive,
      std::function<bool()> current, std::function<uint64_t()> now,
      std::function<uint64_t()> utc, std::function<void(const char*)> phase,
      PostPort post, const TurnCorrelation& opening,
      const ConversationRuntimeSnapshot& snapshot,
      std::function<bool(const TurnOperationObservation&)> completed);
  bool Current() const;
  bool Event(const RealtimeEvent& event);
  bool SendInput(const std::vector<uint8_t>& pcm, uint32_t capturedEpoch);
  bool Uploading() const { return uploading_.load(); }
  uint32_t InputEpoch() const { return inputEpoch_.load(); }
  uint64_t ResponseGeneration() const { return responseGeneration_; }
  bool Failed() const { return failed_.load(); }
  bool Closed() const { return closed_; }
  void Fail() { failed_.store(true); }
  void Stop() { stop_.store(true); uploading_.store(false); }
  const std::string& Error() const { return error_; }
 private:
  int Post(const std::string& path, const void* data, size_t size,
      const char* type, const std::string& inputId = "", unsigned sequence = 0);
  VoiceAudioPort& audio_;
  bool gptLive_, segmentPlaying_ = false;
  std::string endpoint_, conversationId_, streamId_, inputId_, operationId_, error_;
  std::function<bool()> current_;
  std::function<uint64_t()> now_, utc_;
  std::function<void(const char*)> phase_;
  PostPort post_;
  TurnCorrelation expected_;
  ConversationRuntimeSnapshot snapshot_;
  std::function<bool(const TurnOperationObservation&)> completed_;
  std::mutex inputMutex_;
  std::atomic<bool> stop_{false}, uploading_{false}, failed_{false};
  std::atomic<uint32_t> inputEpoch_{0};
  bool sessionSeen_ = false, terminal_ = false, closed_ = false;
  bool responseRestartPending_ = false;
  unsigned sequence_ = 0, turns_ = 0;
  size_t audioBytes_ = 0, inputBytes_ = 0;
  uint64_t responseGeneration_ = 0;
  std::atomic<uint64_t> startedAtMs_{0}, lastTurnAtMs_{0};
};
bool DecodeRealtimePcm(const std::string& encoded, uint8_t* output, size_t capacity, size_t& length);

#pragma once
#include <atomic>
#include <functional>
#include <mutex>
#include <array>
#include "VoiceAudioPort.h"
#include "VoicePlaybackPort.h"
#include "VoiceControlPort.h"
#include "RealtimeEventCodec.h"
#include "../conversation/ConversationRuntimeState.h"

// Both platforms use this owner of the production event/input state machine.
// Network, clocks and media are platform ports; no platform may select a slot.
class RealtimeSessionEngine final {
 public:
  using PostPort = std::function<int(const std::string&, const void*, size_t,
      const char*, const std::string&, unsigned)>;
  struct AsyncPorts {
    VoicePlaybackPort& playback;
    VoiceControlPort& control;
    std::function<void(bool)> capture;
  };
  RealtimeSessionEngine(VoiceAudioPort& audio, bool gptLive,
      std::function<bool()> current, std::function<uint64_t()> now,
      std::function<uint64_t()> utc, std::function<void(const char*)> phase,
      PostPort post, const TurnCorrelation& opening,
      const ConversationRuntimeSnapshot& snapshot,
      std::function<bool(const TurnOperationObservation&)> completed);
  RealtimeSessionEngine(VoiceAudioPort& audio, bool gptLive,
      std::function<bool()> current, std::function<uint64_t()> now,
      std::function<uint64_t()> utc, std::function<void(const char*)> phase,
      PostPort post, const TurnCorrelation& opening,
      const ConversationRuntimeSnapshot& snapshot,
      std::function<bool(const TurnOperationObservation&)> completed, AsyncPorts ports);
  bool Current() const;
  bool Event(const RealtimeEvent& event);
  bool Audio(uint64_t generation, const uint8_t* pcm, size_t length);
  bool Poll();
  bool HasPendingActions() const { return actionCount_ != 0 || cancelPending_; }
  bool SendInput(const std::vector<uint8_t>& pcm, uint32_t capturedEpoch);
  bool Uploading() const { return uploading_.load(); }
  uint32_t InputEpoch() const { return inputEpoch_.load(); }
  uint64_t ResponseGeneration() const { return responseGeneration_; }
  bool Failed() const { return failed_.load(); }
  bool Closed() const { return closed_; }
  void Fail();
  void Stop();
  const std::string& Error() const { return error_; }
 private:
  int Post(const std::string& path, const void* data, size_t size,
      const char* type, const std::string& inputId = "", unsigned sequence = 0);
  enum class ActionKind { Pause, Checkpoint, Completed };
  struct Action {
    ActionKind kind = ActionKind::Pause;
    uint64_t generation = 0, admittedAt = 0, turnStartedAt = 0, audioRevision = 0;
    VoicePlaybackPort::DrainHandle drain = 0;
    VoiceControlPort::Handle control = 0;
    bool drained = false;
    VoiceControlPort::Command command;
    TurnOperationObservation operation;
    TurnCorrelation expected;
  };
  bool AdmitAction(ActionKind kind, const RealtimeEvent& event);
  bool ValidCompleted(const TurnOperationObservation& operation, const TurnCorrelation& expected) const;
  void AbandonActions();
  void Capture(bool enabled);
  void PopAction();
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
  VoicePlaybackPort* playback_ = nullptr;
  VoiceControlPort* control_ = nullptr;
  std::function<void(bool)> capture_;
  std::array<Action, 4> actions_;
  size_t actionCount_ = 0;
  bool cancelPending_ = false, closedPending_ = false;
  uint64_t audioRevision_ = 0;
  uint64_t lastDrainHandle_ = 0, lastControlHandle_ = 0;
  std::mutex inputMutex_;
  std::atomic<bool> stop_{false}, uploading_{false}, failed_{false};
  std::atomic<uint32_t> inputEpoch_{0};
  bool sessionSeen_ = false, terminal_ = false, closed_ = false;
  bool responseRestartPending_ = false;
  unsigned sequence_ = 0, turns_ = 0;
  size_t audioBytes_ = 0, inputBytes_ = 0;
  bool inputInFlight_ = false;
  uint64_t inputClaim_ = 0;
  uint64_t responseGeneration_ = 0;
  std::atomic<uint64_t> startedAtMs_{0}, lastTurnAtMs_{0};
};
bool DecodeRealtimePcm(const std::string& encoded, uint8_t* output, size_t capacity, size_t& length);

#pragma once
#include <array>
#include <mutex>
#include <string>
#include "../conversation/ConversationRuntimeState.h"
#include "DeviceShowAudio.h"

struct DeviceShowCue {
  std::string cueId, duelId, bootId, sceneId, speechText, audioPath;
  std::string cueType = "NarratedBeat", preset, clipId, voice = "marin";
  uint64_t generation = 0, deviceRevision = 0, issuedAtMs = 0, validUntilMs = 0;
  uint8_t round = 0, compartment = 0;
};
struct DeviceShowSettings {
  uint64_t revision = 0;
  bool quiet = true, ambient = false;
  uint64_t stopRevision = 0;
};
struct DeviceShowEnvelope { DeviceShowSettings settings; DeviceShowCue cue; std::string cancelledCueId; };
struct DeviceShowAck {
  DeviceShowCue cue;
  std::string outcome, evidence, reason;
};
bool ParseDeviceShowEnvelope(const std::string& json, DeviceShowEnvelope& envelope);
std::string DeviceShowAckJson(const DeviceShowAck& ack);

// One offer and at most four acknowledgements. Network and control owners only
// exchange data here; no callback or I/O runs while this lock is held.
class DeviceShowMailbox final {
 public:
  enum class Stage { Empty, Offered, Accepted, Playing, AudioFinished };
  struct View {
    DeviceShowCue cue;
    Stage stage = Stage::Empty;
    bool audioSucceeded = false;
    uint64_t executionDeadlineMs = 0;
    std::string audioError;
    std::shared_ptr<DeviceShowAudio> audio;
    bool acceptedAckConfirmed = false;
  };
  void PublishState(bool allowed, const DeviceShowSettings& applied, uint64_t generation, uint64_t revision);
  bool PollState(DeviceShowSettings& applied, uint64_t& generation, uint64_t& revision) const;
  void Settings(const DeviceShowSettings& desired);
  bool TakeSettings(DeviceShowSettings& desired);
  bool Offer(const DeviceShowCue& cue, std::shared_ptr<DeviceShowAudio> audio = {});
  bool IsActive() const;
  void Cancel(const std::string& cueId = "");
  bool IsCancelled(const std::string& cueId) const;
  std::shared_ptr<DeviceShowAudio> TakeRetiredAudio();
  View Read() const;
  bool Accept(uint64_t nowMs);
  bool TakeAudio(DeviceShowCue& cue);
  bool PlaybackCurrent(const std::string& cueId, uint64_t nowMs) const;
  void AudioFinished(const std::string& cueId, bool succeeded, const char* error);
  void Finish(const char* outcome, const char* evidence, const char* reason);
  bool ReadAck(DeviceShowAck& ack) const;
  void ConfirmAck(const std::string& cueId, const std::string& outcome);
 private:
  mutable std::mutex mutex_;
  bool allowed_ = false, desiredPending_ = false;
  DeviceShowSettings applied_, desired_;
  uint64_t generation_ = 0, revision_ = 0;
  View current_;
  std::string cancelledCueId_;
  std::shared_ptr<DeviceShowAudio> retiredAudio_;
  std::array<DeviceShowAck, 4> acks_{};
  size_t ackHead_ = 0, ackCount_ = 0;
};

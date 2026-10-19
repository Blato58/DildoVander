#pragma once
#include "DeviceShowCue.h"
#include "../offline_show/OfflineShow.h"

// Control-task adapter only. The network task supplies data and acknowledgements;
// every LED still passes through the existing fenced SceneIntentBridge.
class DeviceShowRuntime final {
 public:
  DeviceShowRuntime(DeviceControlRuntime& runtime, ConversationRuntimeState& state,
      PhysicalPresentation& physical, kufr::offline::OfflineShow& offline,
      kufr::offline::AudioPort& audio, DeviceShowMailbox& mailbox, const std::string& bootId)
      : runtime_(runtime), state_(state), scene_(physical), offline_(offline),
        audio_(audio), mailbox_(mailbox), bootId_(bootId) {}
  void Tick(uint64_t nowMs, bool physicalAvailable, bool audioAvailable = true);
  bool TakeRemoteStop() { const bool stopped = remoteStop_; remoteStop_ = false; return stopped; }
  bool Active() const { return phase_ != Phase::Idle || mailbox_.IsActive(); }
  void SetVoice(const std::string& voice) { voice_ = voice; }
 private:
  enum class Phase { Idle, Led, AwaitAudio, Playback, Cancelling };
  struct Seen { std::string id; uint64_t atMs = 0, fingerprint = 0; };
  void Finish(DeviceShowMailbox::View& view, const char* outcome, const char* evidence, const char* reason);
  static uint64_t Fingerprint(const DeviceShowCue& cue);
  DeviceControlRuntime& runtime_;
  ConversationRuntimeState& state_;
  SceneIntentBridge scene_;
  kufr::offline::OfflineShow& offline_;
  kufr::offline::AudioPort& audio_;
  DeviceShowMailbox& mailbox_;
  std::string bootId_;
  std::string voice_ = "marin";
  Phase phase_ = Phase::Idle;
  uint64_t ownedGeneration_ = 0;
  bool ownsReservation_ = false, remoteStop_ = false;
  std::shared_ptr<DeviceShowAudio> playingAudio_;
  kufr::offline::Clip clip_{};
  std::array<Seen, 32> seen_{};
  size_t seenCursor_ = 0;
};

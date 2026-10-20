#pragma once
#include <atomic>
#include "BoundedPlaybackOwner.h"
#include "VoicePlaybackPort.h"

// A reader keeps this facade for its entire original session. Old-session
// cleanup is scoped and cannot stop a newer session using the shared worker.
class BoundedVoicePlayback final : public VoicePlaybackPort {
 public:
  BoundedVoicePlayback(BoundedPlaybackOwner& owner, uint64_t session)
      : owner_(owner), session_(session) {}
  Progress Begin();
  bool Queue(uint64_t generation, const uint8_t* pcm, size_t length) override;
  Progress Drain(uint64_t generation) override;
  Progress Cancel(uint64_t generation) override;
  bool Failed() const override;
  void Stop() override;
 private:
  enum class Waiting { None, Begin, Cancel, Drain };
  Progress Poll();
  Progress Fail();
  BoundedPlaybackOwner::Scope Scope() const { return {session_, generation_}; }
  BoundedPlaybackOwner& owner_;
  const uint64_t session_;
  uint64_t generation_ = 0, sequence_ = 0, samples_ = 0, drainedSamples_ = 0;
  BoundedPlaybackOwner::Handle pending_;
  Waiting waiting_ = Waiting::None;
  bool started_ = false, acquired_ = false;
  std::atomic<bool> failed_{false};
};

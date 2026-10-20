#include "BoundedVoicePlayback.h"
#include <limits>

using Owner = BoundedPlaybackOwner;
using Progress = VoicePlaybackPort::Progress;

Progress BoundedVoicePlayback::Fail() {
  failed_ = true;
  if (acquired_) owner_.StopSession(session_);
  return Progress::Failed;
}
Progress BoundedVoicePlayback::Poll() {
  const auto result = owner_.Poll(pending_);
  if (result.progress == Owner::Progress::Pending) return Progress::Pending;
  if (result.progress != Owner::Progress::Succeeded) return Fail();
  if (waiting_ == Waiting::Drain) drainedSamples_ = pending_.Samples();
  waiting_ = Waiting::None;
  return Progress::Succeeded;
}
Progress BoundedVoicePlayback::Begin() {
  if (failed_) return Progress::Failed;
  if (started_) return waiting_ == Waiting::Begin ? Poll() : Progress::Succeeded;
  started_ = true;
  const auto result = owner_.RequestStart(Scope(), pending_);
  if (result.progress != Owner::Progress::Pending) return Fail();
  acquired_ = true;
  waiting_ = Waiting::Begin;
  return Progress::Pending;
}
bool BoundedVoicePlayback::Queue(uint64_t generation, const uint8_t* pcm, size_t length) {
  if (!started_ || failed_ || generation != generation_ || waiting_ == Waiting::Begin)
    return false;
  if (sequence_ == std::numeric_limits<uint64_t>::max() || samples_ > std::numeric_limits<uint64_t>::max() - length / 2) {
    Fail(); return false;
  }
  if (owner_.Enqueue(Scope(), sequence_, pcm, length).progress != Owner::Progress::Succeeded) {
    Fail(); return false;
  }
  ++sequence_; samples_ += length / 2;
  return true;
}
Progress BoundedVoicePlayback::Drain(uint64_t generation) {
  if (!started_ || failed_ || generation != generation_) return Fail();
  if (waiting_ != Waiting::None) {
    const bool originalDrain = waiting_ == Waiting::Drain;
    const auto result = Poll();
    if (result != Progress::Succeeded) return result;
    if (originalDrain) return Progress::Succeeded;
  }
  if (samples_ == drainedSamples_) return Progress::Succeeded;
  if (owner_.RequestDrain(Scope(), sequence_ - 1, samples_, pending_).progress != Owner::Progress::Pending) return Fail();
  waiting_ = Waiting::Drain;
  return Progress::Pending;
}
Progress BoundedVoicePlayback::Cancel(uint64_t generation) {
  if (!started_ || failed_ || generation < generation_ || waiting_ == Waiting::Begin) return Fail();
  if (generation == generation_) {
    if (waiting_ == Waiting::Cancel) return Poll();
    return Progress::Succeeded;
  }
  if (owner_.RequestCancel(Scope(), generation, pending_).progress != Owner::Progress::Pending) return Fail();
  generation_ = generation;
  sequence_ = samples_ = drainedSamples_ = 0;
  waiting_ = Waiting::Cancel;
  return Progress::Pending;
}
bool BoundedVoicePlayback::Failed() const {
  const auto snapshot = owner_.Inspect();
  return failed_ || snapshot.scope.session != session_ || snapshot.state == Owner::State::Failed
      || snapshot.state == Owner::State::Stopped;
}
void BoundedVoicePlayback::Stop() { if (acquired_) owner_.StopSession(session_); }

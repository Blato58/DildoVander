#include "BoundedVoicePlayback.h"
#include <limits>

using Owner = BoundedPlaybackOwner;
using Progress = VoicePlaybackPort::Progress;

namespace {
uint64_t NextDrainTicket() {
  static std::atomic<uint64_t> next{1};
  auto candidate = next.load();
  while (candidate != std::numeric_limits<uint64_t>::max()) {
    if (next.compare_exchange_weak(candidate, candidate + 1)) return candidate;
  }
  return 0;
}
}

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
  for (const auto& drain : drains_) if (drain.ticket) return Fail();
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
bool BoundedVoicePlayback::RequestDrain(uint64_t generation, DrainHandle& handle) {
  handle = 0;
  if (!started_ || failed_ || !CurrentScope() || generation != generation_ || waiting_ == Waiting::Begin || waiting_ == Waiting::Drain)
    return false;
  DrainReceipt* empty = nullptr;
  for (auto& drain : drains_) if (!drain.ticket) { empty = &drain; break; }
  if (!empty) return false;
  const auto ticket = NextDrainTicket();
  if (!ticket) return false;
  DrainReceipt receipt;
  receipt.ticket = ticket; receipt.generation = generation;
  receipt.sequence = sequence_ ? sequence_ - 1 : 0; receipt.samples = samples_;
  receipt.complete = samples_ == drainedSamples_;
  if (!receipt.complete && owner_.ReserveDrain(Scope(), receipt.sequence, receipt.samples, receipt.raw).progress != Owner::Progress::Pending)
    return false;
  *empty = receipt;
  handle = ticket;
  return true;
}
Progress BoundedVoicePlayback::PollDrain(DrainHandle handle) {
  DrainReceipt* receipt = nullptr;
  for (auto& drain : drains_) if (handle && drain.ticket == handle) { receipt = &drain; break; }
  if (!receipt || receipt->generation != generation_ || failed_ || !CurrentScope()) return Progress::Failed;
  if (!receipt->complete) {
    const auto result = owner_.Poll(receipt->raw);
    if (result.progress == Owner::Progress::Pending) return Progress::Pending;
    if (result.progress != Owner::Progress::Succeeded) return Fail();
    drainedSamples_ = receipt->samples;
    // Multiple metadata actions can share exactly one immutable raw frontier.
    // Their facade receipts remain distinct and individually single-use.
    for (auto& drain : drains_) {
      if (drain.ticket && drain.generation == receipt->generation && drain.sequence == receipt->sequence &&
          drain.samples == receipt->samples) drain.complete = true;
    }
  }
  *receipt = {};
  return Progress::Succeeded;
}
void BoundedVoicePlayback::AbandonDrain(DrainHandle handle) {
  // Caller follows abandonment with Cancel(new generation) or scoped Stop.
  // Never convert a discarded metadata action into a raw completion receipt.
  for (auto& drain : drains_) if (handle && drain.ticket == handle) { drain = {}; return; }
}
Progress BoundedVoicePlayback::Cancel(uint64_t generation) {
  if (!started_ || failed_ || generation < generation_ || waiting_ == Waiting::Begin) return Fail();
  if (generation == generation_) {
    if (waiting_ == Waiting::Cancel) return Poll();
    return Progress::Succeeded;
  }
  if (owner_.RequestCancel(Scope(), generation, pending_).progress != Owner::Progress::Pending) return Fail();
  for (auto& drain : drains_) drain = {};
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
bool BoundedVoicePlayback::CurrentScope() const {
  const auto snapshot = owner_.Inspect();
  return snapshot.scope == Scope() && snapshot.state != Owner::State::Failed && snapshot.state != Owner::State::Stopped;
}
void BoundedVoicePlayback::Stop() { if (acquired_) owner_.StopSession(session_); }

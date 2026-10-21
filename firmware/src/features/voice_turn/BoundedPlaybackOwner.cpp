#include "BoundedPlaybackOwner.h"

#include <algorithm>
#include <atomic>
#include <cstring>
#include <limits>

constexpr size_t BoundedPlaybackOwner::SlotBytes;
constexpr size_t BoundedPlaybackOwner::SlotCount;
constexpr size_t BoundedPlaybackOwner::WorkspaceBytes;
constexpr size_t BoundedPlaybackOwner::MaximumPcmBytes;

namespace {
constexpr uint64_t MaximumCounter = std::numeric_limits<uint64_t>::max();
using Owner = BoundedPlaybackOwner;
Owner::Result Pending() { return {Owner::Progress::Pending}; }
Owner::Result Rejected(Owner::Error error) { return {Owner::Progress::Rejected, error}; }
}

BoundedPlaybackOwner::BoundedPlaybackOwner(Port& port, uint8_t* workspace, size_t length,
    ImmediateStop stop, ImmediateMute mute, void* context)
    : port_(port), workspace_(workspace), incarnation_(NextIncarnation()),
      configured_(incarnation_ && workspace && length >= WorkspaceBytes && stop && mute),
      immediateStop_(stop), immediateMute_(mute), context_(context) {
  if (configured_) std::memset(workspace_, 0, WorkspaceBytes);
}
uint64_t BoundedPlaybackOwner::NextIncarnation() {
  static std::atomic<uint64_t> next{1};
  uint64_t candidate = next.load();
  while (candidate != MaximumCounter) {
    if (next.compare_exchange_weak(candidate, candidate + 1)) return candidate;
  }
  return 0; // Exhaustion is permanent; no reused object lifetime can mint authority.
}
bool BoundedPlaybackOwner::SameHandle(const Handle& a, const Handle& b) {
  return a.kind_ != Handle::Kind::None && a.owner_ == b.owner_ && a.incarnation_ == b.incarnation_ &&
      a.kind_ == b.kind_ && a.scope_ == b.scope_ &&
      a.sequence_ == b.sequence_ && a.samples_ == b.samples_;
}
bool BoundedPlaybackOwner::CurrentLocked(Scope scope) const {
  return scope == scope_ && state_ != State::Stopped && state_ != State::Failed;
}
BoundedPlaybackOwner::Handle BoundedPlaybackOwner::NewOperationLocked(
    Handle::Kind kind, Scope scope, uint64_t sequence, uint64_t samples) {
  Handle handle;
  handle.owner_ = this; handle.incarnation_ = incarnation_;
  handle.kind_ = kind; handle.scope_ = scope; handle.sequence_ = sequence; handle.samples_ = samples;
  operation_ = {handle, Pending(), false};
  return handle;
}
uint16_t BoundedPlaybackOwner::QueuedForScrubLocked() {
  uint16_t mask = 0;
  for (size_t i = 0; i != SlotCount; ++i) if (slots_[i].state == SlotState::Queued) {
    slots_[i].state = SlotState::Scrubbing;
    mask |= static_cast<uint16_t>(1u << i);
  }
  return mask;
}
uint16_t BoundedPlaybackOwner::RevokeLocked(State state, Error error) {
  state_ = state;
  if (!operation_.consumed) operation_.result = {Progress::Failed, error};
  for (size_t i = 0; i != drainCount_; ++i) drains_[i].result = {Progress::Failed, error};
  return QueuedForScrubLocked();
}
void BoundedPlaybackOwner::Scrub(uint16_t mask) {
  // Exactly sixteen fixed slots at most. Filling/Playing spans are excluded
  // until their unique producer/worker releases the borrowed workspace bytes.
  for (size_t i = 0; i != SlotCount; ++i) if (mask & (1u << i)) {
    volatile uint8_t* bytes = workspace_ + i * SlotBytes;
    for (size_t byte = 0; byte != SlotBytes; ++byte) bytes[byte] = 0;
    std::lock_guard<std::mutex> lock(mutex_);
    slots_[i] = {};
  }
}
void BoundedPlaybackOwner::StopFence() {
  if (immediateStop_) immediateStop_(context_);
  std::lock_guard<std::mutex> lock(mutex_);
  --fences_;
}
BoundedPlaybackOwner::Result BoundedPlaybackOwner::Fail(Scope expected, Error error) {
  uint16_t scrub;
  {
    std::lock_guard<std::mutex> lock(mutex_);
    if (!CurrentLocked(expected)) return Rejected(Error::Stale);
    scrub = RevokeLocked(State::Failed, error);
    ++fences_;
  }
  StopFence(); Scrub(scrub);
  return {Progress::Failed, error};
}
void BoundedPlaybackOwner::Stop() {
  uint16_t scrub;
  {
    std::lock_guard<std::mutex> lock(mutex_);
    scrub = RevokeLocked(State::Stopped, Error::Stopped);
    ++fences_;
  }
  StopFence(); Scrub(scrub);
}
bool BoundedPlaybackOwner::StopSession(uint64_t expectedSession) {
  uint16_t scrub;
  {
    std::lock_guard<std::mutex> lock(mutex_);
    if (!expectedSession || scope_.session != expectedSession) return false;
    scrub = RevokeLocked(State::Stopped, Error::Stopped);
    ++fences_;
  }
  StopFence(); Scrub(scrub);
  return true;
}
BoundedPlaybackOwner::Result BoundedPlaybackOwner::RequestStart(Scope scope, Handle& handle) {
  uint16_t scrub;
  bool exhausted;
  {
    std::lock_guard<std::mutex> lock(mutex_);
    if (!configured_) return {Progress::Failed, Error::InvalidConfiguration};
    if (!scope.session) return Rejected(Error::InvalidScope);
    if (scope.session <= lastSession_) return Rejected(Error::Stale);
    lastSession_ = scope.session;
    exhausted = scope.session == MaximumCounter || scope.generation == MaximumCounter;
    scrub = RevokeLocked(exhausted ? State::Failed : State::Starting,
        exhausted ? Error::CounterExhausted : Error::Stale);
    if (!exhausted) {
      scope_ = scope;
      acceptedSequence_ = acceptedSamples_ = playedSamples_ = lastDrainSamples_ = 0;
      drainCount_ = 0;
      handle = NewOperationLocked(Handle::Kind::Start, scope);
    }
    ++fences_;
  }
  StopFence(); Scrub(scrub);
  return exhausted ? Result{Progress::Failed, Error::CounterExhausted} : Pending();
}
BoundedPlaybackOwner::Result BoundedPlaybackOwner::RequestCancel(
    Scope scope, uint64_t nextGeneration, Handle& handle) {
  uint16_t scrub;
  bool exhausted;
  {
    std::lock_guard<std::mutex> lock(mutex_);
    if (!CurrentLocked(scope)) return Rejected(Error::Stale);
    if (state_ != State::Ready && state_ != State::Draining && state_ != State::Cancelling) return Rejected(Error::Busy);
    if (nextGeneration <= scope.generation) return Rejected(Error::Stale);
    exhausted = nextGeneration == MaximumCounter;
    scrub = RevokeLocked(exhausted ? State::Failed : State::Cancelling,
        exhausted ? Error::CounterExhausted : Error::Stale);
    if (!exhausted) {
      scope_.generation = nextGeneration;
      acceptedSequence_ = acceptedSamples_ = playedSamples_ = lastDrainSamples_ = 0;
      drainCount_ = 0;
      handle = NewOperationLocked(Handle::Kind::Cancel, scope_);
    }
    ++fences_;
  }
  if (exhausted) {
    StopFence(); Scrub(scrub);
    return {Progress::Failed, Error::CounterExhausted};
  }
  const bool muted = immediateMute_(context_);
  bool current;
  {
    std::lock_guard<std::mutex> lock(mutex_);
    current = SameHandle(handle, operation_.handle) && !operation_.consumed && CurrentLocked(handle.scope_);
    if (current && !muted) {
      scrub |= RevokeLocked(State::Failed, Error::FenceFailure);
      // Transfer the outstanding mute fence to Stop without exposing a window
      // in which the worker could flush/unmute a generation whose fence failed.
    } else --fences_;
  }
  if (current && !muted) StopFence();
  Scrub(scrub);
  if (!current) return Rejected(Error::Stale);
  return muted ? Pending() : Result{Progress::Failed, Error::FenceFailure};
}
BoundedPlaybackOwner::Result BoundedPlaybackOwner::Enqueue(
    Scope scope, uint64_t sequence, const uint8_t* pcm, size_t length) {
  uint16_t reserved = 0;
  Error failure = Error::None;
  {
    std::lock_guard<std::mutex> lock(mutex_);
    if (!CurrentLocked(scope)) return Rejected(Error::Stale);
    if ((state_ != State::Ready && state_ != State::Cancelling && state_ != State::Draining) || filling_) return Rejected(Error::Busy);
    if (!pcm || !length || length > MaximumPcmBytes || length % 2) return Rejected(Error::InvalidPcm);
    if (sequence == MaximumCounter || acceptedSamples_ > MaximumCounter - length / 2)
      failure = Error::CounterExhausted;
    else if (sequence != (acceptedSamples_ ? acceptedSequence_ + 1 : 0)) failure = Error::SequenceMismatch;
    else {
      size_t free = 0;
      for (const auto& slot : slots_) free += slot.state == SlotState::Free;
      const size_t needed = (length + SlotBytes - 1) / SlotBytes;
      if (free < needed) failure = Error::Overload;
      else {
        size_t offset = 0;
        for (size_t i = 0; i != SlotCount && offset != length; ++i) if (slots_[i].state == SlotState::Free) {
          const size_t bytes = std::min(SlotBytes, length - offset);
          slots_[i] = {SlotState::Filling, scope, bytes, acceptedSamples_ + offset / 2};
          reserved |= static_cast<uint16_t>(1u << i);
          offset += bytes;
        }
        filling_ = true;
      }
    }
  }
  if (failure != Error::None) return Fail(scope, failure);
  size_t offset = 0;
  for (size_t i = 0; i != SlotCount; ++i) if (reserved & (1u << i)) {
    // A Filling slot is exclusively owned by this producer, including on Stop.
    const size_t bytes = std::min(SlotBytes, length - offset);
    std::memcpy(workspace_ + i * SlotBytes, pcm + offset, bytes);
    offset += bytes;
  }
  bool accepted;
  {
    std::lock_guard<std::mutex> lock(mutex_);
    accepted = CurrentLocked(scope) && (state_ == State::Ready || state_ == State::Cancelling || state_ == State::Draining);
    for (size_t i = 0; i != SlotCount; ++i) if (reserved & (1u << i))
      slots_[i].state = accepted ? SlotState::Queued : SlotState::Scrubbing;
    filling_ = false;
    if (accepted) { acceptedSequence_ = sequence; acceptedSamples_ += length / 2; }
  }
  if (!accepted) { Scrub(reserved); return Rejected(Error::Stale); }
  return {Progress::Succeeded};
}
BoundedPlaybackOwner::Result BoundedPlaybackOwner::RequestDrain(
    Scope scope, uint64_t sequence, uint64_t samples, Handle& handle) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (!CurrentLocked(scope)) return Rejected(Error::Stale);
  if (state_ != State::Ready || filling_ || (!operation_.consumed &&
      (operation_.handle.kind_ == Handle::Kind::Start || operation_.handle.kind_ == Handle::Kind::Cancel)))
    return Rejected(Error::Busy);
  return ReserveDrainLocked(scope, sequence, samples, handle);
}
BoundedPlaybackOwner::Result BoundedPlaybackOwner::ReserveDrain(
    Scope scope, uint64_t sequence, uint64_t samples, Handle& handle) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (!CurrentLocked(scope)) return Rejected(Error::Stale);
  if (filling_ || (state_ != State::Ready && state_ != State::Draining && state_ != State::Cancelling))
    return Rejected(Error::Busy);
  return ReserveDrainLocked(scope, sequence, samples, handle);
}
BoundedPlaybackOwner::Result BoundedPlaybackOwner::ReserveDrainLocked(
    Scope scope, uint64_t sequence, uint64_t samples, Handle& handle) {
  if (!samples || sequence != acceptedSequence_ || samples != acceptedSamples_ || samples <= lastDrainSamples_)
    return Rejected(Error::FrontierMismatch);
  if (drainCount_ && drains_[drainCount_ - 1].handle.samples_ == samples) {
    handle = drains_[drainCount_ - 1].handle;
    return Pending();
  }
  if (drainCount_ == drains_.size()) return Rejected(Error::Busy);
  handle = {};
  handle.owner_ = this; handle.incarnation_ = incarnation_;
  handle.kind_ = Handle::Kind::Drain; handle.scope_ = scope;
  handle.sequence_ = sequence; handle.samples_ = samples;
  drains_[drainCount_++] = {handle, Pending(), false};
  if (state_ != State::Cancelling) state_ = State::Draining;
  return Pending();
}
BoundedPlaybackOwner::Result BoundedPlaybackOwner::Poll(const Handle& handle) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (handle.kind_ == Handle::Kind::Drain) {
    size_t index = 0;
    while (index != drainCount_ && !SameHandle(handle, drains_[index].handle)) ++index;
    if (index == drainCount_) return Rejected(Error::InvalidHandle);
    const auto result = drains_[index].result;
    if (index || result.progress == Progress::Pending) return result;
    for (size_t i = 1; i != drainCount_; ++i) drains_[i - 1] = drains_[i];
    drains_[--drainCount_] = {};
    if (state_ == State::Draining && result.progress == Progress::Succeeded) {
      lastDrainSamples_ = handle.samples_;
      state_ = drainCount_ ? State::Draining : State::Ready;
    }
    return result;
  }
  if (!SameHandle(handle, operation_.handle) || operation_.consumed) return Rejected(Error::InvalidHandle);
  const auto result = operation_.result;
  if (result.progress != Progress::Pending) {
    operation_.consumed = true;
  }
  return result;
}
bool BoundedPlaybackOwner::FenceCurrent(void* context, Scope scope) {
  const auto& claim = *static_cast<WorkerClaim*>(context);
  auto& owner = *claim.owner;
  std::lock_guard<std::mutex> lock(owner.mutex_);
  if (scope != claim.scope || !owner.CurrentLocked(scope) || !owner.workerActive_ ||
      owner.workerKind_ != claim.kind) return false;
  if (claim.kind == WorkKind::Play) {
    const auto& slot = owner.slots_[claim.slot];
    return (owner.state_ == State::Ready || owner.state_ == State::Draining) &&
        slot.state == SlotState::Playing && slot.scope == scope && slot.firstSample == claim.firstSample;
  }
  if (claim.kind == WorkKind::Drain) {
    return owner.drainCount_ && SameHandle(claim.operation, owner.drains_[0].handle) &&
        owner.state_ == State::Draining && owner.drains_[0].result.progress == Progress::Pending;
  }
  if (!SameHandle(claim.operation, owner.operation_.handle) || owner.operation_.consumed) return false;
  if (claim.kind == WorkKind::Begin) return owner.state_ == State::Starting;
  if (claim.kind == WorkKind::Cancel) return owner.state_ == State::Cancelling;
  return owner.state_ == State::Draining && owner.operation_.result.progress == Progress::Pending;
}
bool BoundedPlaybackOwner::RunOne() {
  WorkKind work;
  Scope scope;
  Handle operation;
  size_t index = SlotCount, bytes = 0;
  uint64_t firstSample = 0;
  {
    std::lock_guard<std::mutex> lock(mutex_);
    if (!configured_ || workerActive_ || fences_) return false;
    scope = scope_; operation = state_ == State::Draining && drainCount_ ? drains_[0].handle : operation_.handle;
    if (state_ == State::Starting) work = WorkKind::Begin;
    else if (state_ == State::Cancelling) work = WorkKind::Cancel;
    else if (state_ == State::Ready || state_ == State::Draining) {
      // The barrier is immutable even if reader enqueue advances the accepted
      // frontier. A completed but unconsumed proof also holds all later PCM.
      if (state_ == State::Draining && playedSamples_ == operation.samples_) {
        if (drains_[0].result.progress != Progress::Pending) return false;
        work = WorkKind::Drain;
      } else {
        for (size_t i = 0; i != SlotCount; ++i) if (slots_[i].state == SlotState::Queued && slots_[i].scope == scope &&
            (state_ != State::Draining || slots_[i].firstSample + slots_[i].bytes / 2 <= operation.samples_) &&
            (index == SlotCount || slots_[i].firstSample < slots_[index].firstSample)) index = i;
        if (index == SlotCount) return false;
        slots_[index].state = SlotState::Playing;
        bytes = slots_[index].bytes; firstSample = slots_[index].firstSample;
        work = WorkKind::Play;
      }
    } else return false;
    workerKind_ = work;
    workerActive_ = true;
  }
  WorkerClaim claim{this, scope, work, operation, index, firstSample};
  const Fence fence{scope, &claim, FenceCurrent};
  bool succeeded = false;
  if (work == WorkKind::Begin) succeeded = port_.Begin(fence);
  else if (work == WorkKind::Cancel) succeeded = port_.CancelPlayback(fence);
  else if (work == WorkKind::Drain) succeeded = port_.Drain(fence);
  else succeeded = port_.Play(fence, workspace_ + index * SlotBytes, bytes);
  bool fail = false;
  {
    std::lock_guard<std::mutex> lock(mutex_);
    if (work == WorkKind::Play) slots_[index].state = SlotState::Scrubbing;
    workerActive_ = false;
    if (CurrentLocked(scope)) {
      if (!succeeded || (work == WorkKind::Play && firstSample != playedSamples_)) fail = true;
      else if (work == WorkKind::Play) playedSamples_ += bytes / 2;
      else if (work == WorkKind::Drain && drainCount_ && SameHandle(operation, drains_[0].handle)) {
        drains_[0].result = {Progress::Succeeded};
      }
      else if (SameHandle(operation, operation_.handle) && !operation_.consumed) {
        operation_.result = {Progress::Succeeded};
        if (work == WorkKind::Begin || work == WorkKind::Cancel) state_ = drainCount_ ? State::Draining : State::Ready;
      }
    }
  }
  if (work == WorkKind::Play) Scrub(static_cast<uint16_t>(1u << index));
  if (fail) Fail(scope, Error::IoFailure);
  return true;
}
BoundedPlaybackOwner::Snapshot BoundedPlaybackOwner::Inspect() const {
  std::lock_guard<std::mutex> lock(mutex_);
  size_t free = 0, queued = 0;
  for (const auto& slot : slots_) {
    if (slot.state == SlotState::Free) free += SlotBytes;
    else if ((slot.state == SlotState::Queued || slot.state == SlotState::Playing) && slot.scope == scope_)
      queued += slot.bytes;
  }
  return {state_, scope_, configured_ ? free : 0, queued, acceptedSequence_, acceptedSamples_, playedSamples_, workerActive_};
}

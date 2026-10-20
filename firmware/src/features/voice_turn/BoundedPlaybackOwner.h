#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <mutex>

// RAM admission belongs to reader/control; exactly one worker calls RunOne.
// Port calls, callbacks and PCM copies/scrubs never hold the metadata mutex.
// Caller keeps the owner/workspace alive until worker, submitters and callbacks
// are quiescent. Port/callback implementations must not throw. PCM passed to
// Enqueue is borrowed only until that call returns.
// Stop/mute callbacks must immediately fence physical output, remain safe beside
// an in-flight Port call, and must not reopen the port. DMA-tail guarantees belong
// to that platform fence, not to this RAM owner.
class BoundedPlaybackOwner final {
 public:
  static constexpr size_t SlotBytes = 4096;
  static constexpr size_t SlotCount = 16;
  static constexpr size_t WorkspaceBytes = SlotBytes * SlotCount;
  static constexpr size_t MaximumPcmBytes = 32768;
  struct Scope {
    uint64_t session = 0, generation = 0;
    bool operator==(const Scope& other) const {
      return session == other.session && generation == other.generation;
    }
    bool operator!=(const Scope& other) const { return !(*this == other); }
  };
  enum class Progress { Pending, Succeeded, Failed, Rejected };
  enum class Error {
    None, InvalidConfiguration, InvalidScope, Stale, Busy, InvalidPcm,
    SequenceMismatch, FrontierMismatch, Overload, CounterExhausted,
    IoFailure, FenceFailure, Stopped, InvalidHandle
  };
  struct Result {
    Progress progress;
    Error error = Error::None;
  };
  enum class State { Stopped, Starting, Ready, Cancelling, Draining, Failed };
  struct Fence {
    Scope scope;
    void* context;
    bool (*isCurrent)(void*, Scope);
    bool Current() const { return isCurrent && isCurrent(context, scope); }
  };
  // Fence is borrowed only for the synchronous Port call. Recheck Current under
  // the platform output-enable/active-state gate, and before every hardware
  // write. It must fence late Begin/unmute as well as late playback writes.
  class Port {
   public:
    virtual ~Port() = default;
    virtual bool Begin(const Fence& fence) = 0;
    // Success means the entire span was accepted; failure may include a prefix.
    virtual bool Play(const Fence& fence, const uint8_t* pcm, size_t length) = 0;
    virtual bool Drain(const Fence& fence) = 0;
    virtual bool CancelPlayback(const Fence& fence) = 0;
  };
  class Handle {
   public:
    Scope Target() const { return scope_; }
    uint64_t Sequence() const { return sequence_; }
    uint64_t Samples() const { return samples_; }
   private:
    friend class BoundedPlaybackOwner;
    enum class Kind { None, Start, Cancel, Drain } kind_ = Kind::None;
    const BoundedPlaybackOwner* owner_ = nullptr;
    uint64_t incarnation_ = 0;
    Scope scope_;
    uint64_t sequence_ = 0, samples_ = 0;
  };
  struct Snapshot {
    State state;
    Scope scope;
    size_t freeBytes, queuedBytes;
    uint64_t acceptedSequence, acceptedSamples, playedSamples;
    bool workerActive;
  };
  using ImmediateStop = void (*)(void* context);
  using ImmediateMute = bool (*)(void* context);
  BoundedPlaybackOwner(Port& port, uint8_t* workspace, size_t length,
      ImmediateStop stop, ImmediateMute mute, void* context);
  BoundedPlaybackOwner(const BoundedPlaybackOwner&) = delete;
  BoundedPlaybackOwner& operator=(const BoundedPlaybackOwner&) = delete;

  // Session IDs strictly increase across Stop; generation zero is valid.
  // UINT64_MAX cannot create session/generation/sequence authority.
  Result RequestStart(Scope scope, Handle& handle);
  // Invalidates the old generation before calling the immediate mute fence.
  // New-scope PCM may queue immediately; raw CancelPlayback must succeed
  // before any of it plays. A newer Cancel can supersede an in-flight flush.
  Result RequestCancel(Scope scope, uint64_t nextGeneration, Handle& handle);
  // Succeeded means atomic RAM enqueue, never physical playback completion.
  // Sequence starts at zero for each generation. Full capacity fails/stops.
  Result Enqueue(Scope scope, uint64_t sequence, const uint8_t* pcm, size_t length);
  // Capture the exact accepted sequence/sample frontier. Later same-scope PCM
  // may queue behind it, but cannot play until its single-use result is consumed.
  Result RequestDrain(Scope scope, uint64_t sequence, uint64_t samples, Handle& handle);
  Result Poll(const Handle& handle);
  void Stop();
  // Cleanup from an older engine cannot mute or alter a successor session.
  bool StopSession(uint64_t expectedSession);
  // True means one raw operation was attempted; false is idle/pending, not failure.
  bool RunOne();
  Snapshot Inspect() const;

 private:
  enum class WorkKind { Begin, Cancel, Play, Drain };
  struct WorkerClaim {
    BoundedPlaybackOwner* owner;
    Scope scope;
    WorkKind kind;
    Handle operation;
    size_t slot;
    uint64_t firstSample;
  };
  static bool FenceCurrent(void* context, Scope scope);
  enum class SlotState { Free, Filling, Queued, Playing, Scrubbing };
  struct Slot {
    SlotState state = SlotState::Free;
    Scope scope;
    size_t bytes = 0;
    uint64_t firstSample = 0;
  };
  struct Operation {
    Handle handle;
    Result result{Progress::Rejected, Error::InvalidHandle};
    bool consumed = true;
  };
  static uint64_t NextIncarnation();
  static bool SameHandle(const Handle& a, const Handle& b);
  bool CurrentLocked(Scope scope) const;
  uint16_t RevokeLocked(State state, Error error);
  uint16_t QueuedForScrubLocked();
  void Scrub(uint16_t slots);
  void StopFence();
  Result Fail(Scope expected, Error error);
  Handle NewOperationLocked(Handle::Kind kind, Scope scope, uint64_t sequence = 0, uint64_t samples = 0);
  Port& port_;
  uint8_t* const workspace_;
  const uint64_t incarnation_;
  const bool configured_;
  ImmediateStop immediateStop_;
  ImmediateMute immediateMute_;
  void* context_;
  mutable std::mutex mutex_;
  std::array<Slot, SlotCount> slots_{};
  Scope scope_;
  State state_ = State::Stopped;
  Operation operation_;
  uint64_t lastSession_ = 0, acceptedSequence_ = 0, acceptedSamples_ = 0, playedSamples_ = 0;
  uint64_t lastDrainSamples_ = 0;
  size_t fences_ = 0;
  WorkKind workerKind_ = WorkKind::Begin;
  bool workerActive_ = false, filling_ = false;
};

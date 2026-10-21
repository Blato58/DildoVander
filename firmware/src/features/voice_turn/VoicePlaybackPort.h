#pragma once
#include <cstddef>
#include <cstdint>

// Reader-owned nonblocking admission/polling. PCM enqueue is never completion
// evidence; only a successful exact drain frontier can authorize a played ACK.
class VoicePlaybackPort {
 public:
  enum class Progress { Pending, Succeeded, Failed };
  using DrainHandle = uint64_t;
  virtual ~VoicePlaybackPort() = default;
  virtual bool Queue(uint64_t generation, const uint8_t* pcm, size_t length) = 0;
  virtual Progress Drain(uint64_t generation) = 0;
  // Explicit immutable frontiers for a reader that continues receiving PCM.
  // Unsupported legacy ports cannot silently manufacture an async proof.
  virtual bool RequestDrain(uint64_t, DrainHandle&) { return false; }
  virtual Progress PollDrain(DrainHandle) { return Progress::Failed; }
  // Abandon precedes generation cancellation/session Stop; it grants no proof.
  virtual void AbandonDrain(DrainHandle) {}
  virtual Progress Cancel(uint64_t generation) = 0;
  virtual bool Failed() const = 0;
  virtual void Stop() = 0;
};

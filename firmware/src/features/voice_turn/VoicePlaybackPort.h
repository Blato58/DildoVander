#pragma once
#include <cstddef>
#include <cstdint>

// Reader-owned nonblocking admission/polling. PCM enqueue is never completion
// evidence; only a successful exact drain frontier can authorize a played ACK.
class VoicePlaybackPort {
 public:
  enum class Progress { Pending, Succeeded, Failed };
  virtual ~VoicePlaybackPort() = default;
  virtual bool Queue(uint64_t generation, const uint8_t* pcm, size_t length) = 0;
  virtual Progress Drain(uint64_t generation) = 0;
  virtual Progress Cancel(uint64_t generation) = 0;
  virtual bool Failed() const = 0;
  virtual void Stop() = 0;
};

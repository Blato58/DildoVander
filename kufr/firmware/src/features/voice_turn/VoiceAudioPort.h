#pragma once
#include <cstdint>
#include <vector>
class VoiceAudioPort {
 public:
  virtual ~VoiceAudioPort() = default;
  virtual bool Available() const = 0;
  virtual void Begin() {}
  virtual void Listen(bool enabled) = 0;
  virtual bool TakeInput(std::vector<uint8_t>& pcm) = 0;
  virtual bool Play(const std::vector<uint8_t>& pcm) = 0;
  virtual bool Drain() = 0;
  virtual void Stop() = 0;
  // Cancel only output. Unsupported ports fail closed instead of simulating
  // a session-preserving cancellation by reopening a stopped audio device.
  virtual bool CancelPlayback() { return false; }
  virtual bool Failed() const = 0;
};

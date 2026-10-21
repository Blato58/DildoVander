#pragma once
#include <cstdint>
#include <string>

// A port owns one authenticated media session. Submit is bounded RAM admission,
// never an ACK. Each accepted command gets a fresh nonzero monotonic handle and
// one fresh request ID; transport retries retain that exact original envelope.
class VoiceControlPort {
 public:
  enum class Kind { Played, Checkpoint };
  enum class Progress { Pending, Succeeded, Failed };
  using Handle = uint64_t;
  struct Command {
    Kind kind = Kind::Played;
    std::string operationId;
    std::string playbackId;
    uint64_t responseGeneration = 0;
    uint64_t pcmBytes = 0;
  };
  virtual ~VoiceControlPort() = default;
  virtual bool Submit(const Command& command, Handle& handle) = 0;
  virtual Progress Poll(Handle handle) = 0;
  // Retire the authority of this action only. Never cancel another operation or
  // replay an unknown outcome as part of abandoning an old generation.
  virtual void Abandon(Handle handle) = 0;
  // Nonblocking fence for this original port-owned session only.
  virtual void Stop() = 0;
};

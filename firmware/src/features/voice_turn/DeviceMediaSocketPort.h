#pragma once
#include <cstddef>
#include <cstdint>
#include <string>

// All methods, including Close/destruction, belong to one network actor. Urgent
// playback Stop is a separate RAM-only fence; never call this port concurrently.
class DeviceMediaSocketPort {
 public:
  enum class ReceiveResult { Idle, Message, Closed, Failed };
  virtual ~DeviceMediaSocketPort() = default;
  // Bare DNS host, TLS/443 only. No redirect or automatic retry/reconnect.
  virtual bool Open(const std::string& host, const std::string& conversationId,
                    const std::string& token, const std::string& idempotencyKey) = 0;
  // This implementation admits at most 8184 payload bytes; one masked TLS write
  // has a 250ms poll budget. Any partial/unknown write permanently closes it.
  virtual bool Send(bool binary, const uint8_t* data, size_t size) = 0;
  // Keep the same workspace alive across Idle calls until Message/terminal.
  // used is zero on Idle; a partial message is private to the actor in workspace.
  // Caller discards/scrubs partial workspace on terminal results. Receive reads
  // at most one 2048-byte TLS chunk with 50ms poll, plus at most one control reply.
  virtual ReceiveResult Receive(uint8_t* workspace, size_t capacity,
                                size_t& used, bool& binary) = 0;
  virtual void Close() = 0;
  virtual const char* Error() const = 0;
};

#pragma once

#include <array>
#include <mutex>

#include "LocalControlCodec.h"
#include "LocalSocketAdmission.h"

enum class LocalControlEventKind : uint8_t {
  Connected,
  Message,
  Stop,
  Disconnected,
};

struct LocalControlEvent {
  LocalControlEventKind kind = LocalControlEventKind::Connected;
  LocalSocketAdmission::Handle handle;
  uint64_t connectedAtMs = 0;
  LocalControlMessage message;
};

// Network task produces; the sole control owner drains. Close and Stop have
// dedicated slots and cannot be lost when the ordinary bounded queue is full.
class LocalControlInbox final {
 public:
  static constexpr size_t QueueCapacity = 4;

  LocalControlInbox(const std::string& deviceId, const std::string& bootId);

  bool Open(const LocalSocketAdmission::Handle& handle, uint64_t nowMs);
  bool Submit(const LocalSocketAdmission::Handle& handle,
              const LocalControlMessage& message);
  void Disconnect(const LocalSocketAdmission::Handle& handle);
  bool Take(LocalControlEvent& event);

 private:
  bool Matches(const LocalSocketAdmission::Handle& handle) const;
  void ClearQueue();

  std::mutex mutex_;
  std::string deviceId_;
  std::string bootId_;
  LocalSocketAdmission::Handle handle_;
  uint64_t connectedAtMs_ = 0;
  bool connectedPending_ = false;
  bool stopPending_ = false;
  bool disconnectPending_ = false;
  LocalControlMessage stop_;
  std::array<LocalControlMessage, QueueCapacity> queue_;
  size_t head_ = 0;
  size_t count_ = 0;
};

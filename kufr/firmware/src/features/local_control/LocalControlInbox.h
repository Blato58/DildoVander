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
  bool CanProcess(const LocalSocketAdmission::Handle& handle);

  // Bounded, non-reentrant control actions only: no transport/inbox calls.
  // This linearizes a physical transition against Stop/disconnect admission.
  template <typename Action>
  bool RunIfCurrent(const LocalSocketAdmission::Handle& handle,
                    const Action& action) {
    std::lock_guard<std::mutex> lock(mutex_);
    if (!Matches(handle) || stopPending_ || disconnectPending_) {
      return false;
    }
    action();
    return true;
  }

  template <typename Action>
  bool RunIfNoUrgent(const Action& action) {
    std::lock_guard<std::mutex> lock(mutex_);
    if (stopPending_ || disconnectPending_) {
      return false;
    }
    action();
    return true;
  }

  // Used under the maintenance and admission locks for BLE token recovery.
  // A released socket can still have owner events pending; reject until drained.
  // The callback must not reenter the inbox or transport.
  template <typename Action>
  bool RunIfDisconnected(const Action& action) {
    std::lock_guard<std::mutex> lock(mutex_);
    if (handle_.socket >= 0 || handle_.generation != 0 || connectedPending_ ||
        stopPending_ || disconnectPending_ || count_ != 0) {
      return false;
    }
    action();
    return true;
  }

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

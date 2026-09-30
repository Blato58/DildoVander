#include "LocalControlInbox.h"

namespace {

void WipeText(std::string& text) {
  volatile char* bytes = text.empty() ? nullptr : &text[0];
  for (size_t index = 0; index < text.size(); ++index) {
    bytes[index] = 0;
  }
  text.clear();
}

}  // namespace

LocalControlInbox::LocalControlInbox(
    const std::string& deviceId, const std::string& bootId)
    : deviceId_(deviceId), bootId_(bootId) {}

bool LocalControlInbox::Open(
    const LocalSocketAdmission::Handle& handle, uint64_t nowMs) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (handle.socket < 0 || handle.generation == 0 || handle_.socket >= 0 ||
      deviceId_.empty() || bootId_.empty()) {
    return false;
  }
  handle_ = handle;
  connectedAtMs_ = nowMs;
  connectedPending_ = true;
  return true;
}

bool LocalControlInbox::Submit(
    const LocalSocketAdmission::Handle& handle,
    const LocalControlMessage& message) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (!Matches(handle) || disconnectPending_ || message.deviceId != deviceId_ ||
      (message.kind != LocalControlMessageKind::Hello &&
       message.bootId != bootId_)) {
    return false;
  }
  if (message.kind == LocalControlMessageKind::Stop) {
    if (!stopPending_) {
      stop_ = message;
      stopPending_ = true;
    }
    // In-flight commands, including a reset received before Stop, cannot run
    // after the safety transition. Retry requires a new current snapshot.
    ClearQueue();
    return true;
  }
  if (stopPending_ || count_ == QueueCapacity) {
    return false;
  }
  queue_[(head_ + count_) % QueueCapacity] = message;
  ++count_;
  return true;
}

void LocalControlInbox::Disconnect(
    const LocalSocketAdmission::Handle& handle) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (Matches(handle)) {
    disconnectPending_ = true;
    ClearQueue();
  }
}

bool LocalControlInbox::Take(LocalControlEvent& event) {
  std::lock_guard<std::mutex> lock(mutex_);
  WipeText(event.message.text);
  event = {};
  if (handle_.socket < 0) {
    return false;
  }
  event.handle = handle_;
  event.connectedAtMs = connectedAtMs_;
  if (connectedPending_) {
    connectedPending_ = false;
    event.kind = LocalControlEventKind::Connected;
  } else if (stopPending_) {
    stopPending_ = false;
    event.kind = LocalControlEventKind::Stop;
    event.message = std::move(stop_);
    stop_ = {};
  } else if (disconnectPending_) {
    disconnectPending_ = false;
    event.kind = LocalControlEventKind::Disconnected;
    handle_ = {};
    connectedAtMs_ = 0;
  } else if (count_ > 0) {
    event.kind = LocalControlEventKind::Message;
    event.message = queue_[head_];
    WipeText(queue_[head_].text);
    queue_[head_] = {};
    head_ = (head_ + 1) % QueueCapacity;
    --count_;
  } else {
    return false;
  }
  return true;
}

bool LocalControlInbox::Matches(
    const LocalSocketAdmission::Handle& handle) const {
  return handle_.socket >= 0 && handle.socket == handle_.socket &&
         handle.generation == handle_.generation;
}

void LocalControlInbox::ClearQueue() {
  for (LocalControlMessage& message : queue_) {
    WipeText(message.text);
    message = {};
  }
  head_ = 0;
  count_ = 0;
}

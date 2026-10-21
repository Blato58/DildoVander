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
  if (recoveryHeld_ || handle.socket < 0 || handle.generation == 0 || handle_.socket >= 0 ||
      deviceId_.empty() || bootId_.empty()) {
    return false;
  }
  handle_ = handle;
  { std::lock_guard<std::mutex> urgent(urgentMutex_); urgentHandle_ = handle; }
  connectedAtMs_ = nowMs;
  connectedPending_ = true;
  return true;
}

bool LocalControlInbox::BeginRecovery() {
  std::lock_guard<std::mutex> lock(mutex_);
  if (recoveryHeld_ || handle_.socket >= 0 || handle_.generation != 0 || connectedPending_ ||
      stopPending_ || disconnectPending_ || count_ != 0) return false;
  recoveryHeld_ = true;
  return true;
}

void LocalControlInbox::EndRecovery() {
  std::lock_guard<std::mutex> lock(mutex_);
  recoveryHeld_ = false;
}

bool LocalControlInbox::Submit(
    const LocalSocketAdmission::Handle& handle,
    const LocalControlMessage& message) {
  if (message.kind == LocalControlMessageKind::Stop && message.deviceId == deviceId_ && message.bootId == bootId_) {
    // The domain pass may be in a bounded physical bus transaction. This short
    // separate gate is never held through that pass and linearizes only current
    // socket identity plus the immediate OE latch. Ordinary Stop bookkeeping
    // still runs on the sole control owner below.
    std::lock_guard<std::mutex> urgent(urgentMutex_);
    if (urgentStop_ && handle.socket == urgentHandle_.socket &&
        handle.generation != 0 && handle.generation == urgentHandle_.generation)
      urgentStop_(urgentContext_);
  }
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
    { std::lock_guard<std::mutex> urgent(urgentMutex_); urgentHandle_ = {}; }
    disconnectPending_ = true;
    ClearQueue();
  }
}

bool LocalControlInbox::Take(LocalControlEvent& event) {
  std::lock_guard<std::mutex> lock(mutex_);
  WipeText(event.message.text);
  WipeText(event.message.testContext);
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
    WipeText(queue_[head_].testContext);
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

bool LocalControlInbox::CanProcess(
    const LocalSocketAdmission::Handle& handle) {
  std::lock_guard<std::mutex> lock(mutex_);
  return Matches(handle) && !stopPending_ && !disconnectPending_;
}

void LocalControlInbox::ClearQueue() {
  for (LocalControlMessage& message : queue_) {
    WipeText(message.text);
    WipeText(message.testContext);
    message = {};
  }
  head_ = 0;
  count_ = 0;
}

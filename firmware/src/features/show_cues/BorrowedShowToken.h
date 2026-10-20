#pragma once
#include <array>
#include <cstring>
#include <mutex>
#include <stdint.h>

// A read-only bounded copy from the existing token owner. This object has no
// identity, refresh transport or authority to create or extend credentials.
class BorrowedShowToken final {
 public:
  struct Copy {
    std::array<char, 257> bytes{};
    ~Copy() { volatile char* data = bytes.data(); for (size_t i = 0; i < bytes.size(); ++i) data[i] = 0; }
  };
  void Publish(const char* token, uint64_t expiresAtMs) {
    std::lock_guard<std::mutex> lock(mutex_);
    Wipe();
    if (!token) return;
    const size_t length = std::strlen(token);
    if (!length || length >= token_.size()) return;
    std::memcpy(token_.data(), token, length); expiresAtMs_ = expiresAtMs;
  }
  void Clear() { std::lock_guard<std::mutex> lock(mutex_); Wipe(); }
  bool Read(uint64_t nowMs, Copy& copy) const {
    copy.bytes.fill(0);
    std::lock_guard<std::mutex> lock(mutex_);
    if (!token_[0] || nowMs >= expiresAtMs_) return false;
    copy.bytes = token_; return true;
  }
 private:
  void Wipe() {
    volatile char* data = token_.data(); for (size_t i = 0; i < token_.size(); ++i) data[i] = 0;
    expiresAtMs_ = 0;
  }
  mutable std::mutex mutex_;
  std::array<char, 257> token_{};
  uint64_t expiresAtMs_ = 0;
};

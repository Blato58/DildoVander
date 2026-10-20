#pragma once

#include <array>
#include <atomic>
#include <cstdint>
#include <limits>
#include <mutex>

#include "JournalEvidence.h"

// Logical leases, not an NVS-held mutex. A transaction keeps reclamation out
// until its durable result is adopted, including when Stop invalidates motion.
class JournalTransactionArbiter final : public JournalReconciliationBarrier {
 public:
  static constexpr size_t Capacity = 16;
  enum class Purpose : uint8_t { Admission, Evidence, Recovery };
  struct Token {
    uint64_t id = 0;
    uint32_t cancellation = 0;
    Purpose purpose = Purpose::Evidence;
  };
  JournalTransactionArbiter() = default;
  JournalTransactionArbiter(const JournalTransactionArbiter&) = delete;
  JournalTransactionArbiter& operator=(const JournalTransactionArbiter&) = delete;

  bool TryBegin(Purpose purpose, Token& token) {
    token = {};
    if (purpose != Purpose::Admission && purpose != Purpose::Evidence && purpose != Purpose::Recovery) return false;
    std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
    if (!lock || exclusive_ || nextId_ == std::numeric_limits<uint64_t>::max()) return false;
    if (purpose == Purpose::Admission && (stopped_.load() || CancellationExhausted())) return false;
    for (auto& slot : active_) {
      if (slot.id) continue;
      slot = {++nextId_, cancellation_.load(), purpose};
      token = slot;
      return true;
    }
    return false;
  }
  bool Release(const Token& token) {
    std::lock_guard<std::mutex> lock(mutex_);
    for (auto& slot : active_) {
      if (!Same(slot, token)) continue;
      slot = {};
      return true;
    }
    return false;
  }
  bool IsCurrent(const Token& token) const {
    std::lock_guard<std::mutex> lock(mutex_);
    bool present = false;
    for (const auto& slot : active_) present |= Same(slot, token);
    return present && token.cancellation == cancellation_.load() && !CancellationExhausted() &&
        (token.purpose != Purpose::Admission || !stopped_.load());
  }
  // Every urgent invalidation, including an idle Stop, advances this fence.
  // No lock, allocation, storage access or wait for a worker occurs here.
  void CancelAdmissions() {
    InvalidateAdmissions();
    stopped_.store(true);
  }
  void InvalidateAdmissions() {
    uint32_t previous = cancellation_.load();
    while (previous != std::numeric_limits<uint32_t>::max() &&
           !cancellation_.compare_exchange_weak(previous, previous + 1)) {}
  }
  uint32_t Cancellation() const { return cancellation_.load(); }
  bool AdmissionsStopped() const { return stopped_.load(); }
  // Trusted recovery owner only, after adoption/reopen and full reconciliation.
  // A Stop during recovery or any outstanding result prevents reopening.
  bool ResumeAfterRecovery(uint32_t expectedCancellation) {
    std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
    if (!lock || exclusive_ || expectedCancellation != cancellation_.load() || CancellationExhausted()) return false;
    for (const auto& slot : active_) if (slot.id) return false;
    stopped_.store(false);
    // Cancel may race this short publication; its generation wins even if its
    // stopped store happened first. Never undo that concurrent invalidation.
    if (expectedCancellation != cancellation_.load()) {
      stopped_.store(true);
      return false;
    }
    return true;
  }
  bool TryEnter() override {
    std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
    if (!lock || exclusive_) return false;
    for (const auto& slot : active_) if (slot.id) return false;
    exclusive_ = true;
    return true;
  }
  void Leave() override {
    std::lock_guard<std::mutex> lock(mutex_);
    exclusive_ = false;
  }
 private:
  static bool Same(const Token& left, const Token& right) {
    return left.id && left.id == right.id && left.cancellation == right.cancellation && left.purpose == right.purpose;
  }
  bool CancellationExhausted() const { return cancellation_.load() == std::numeric_limits<uint32_t>::max(); }
  mutable std::mutex mutex_;
  std::array<Token, Capacity> active_{};
  uint64_t nextId_ = 0;
  bool exclusive_ = false;
  std::atomic<uint32_t> cancellation_{1};
  std::atomic<bool> stopped_{false};
};

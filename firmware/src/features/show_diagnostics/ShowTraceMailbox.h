#pragma once

#include <atomic>
#include <memory>
#include <mutex>
#include <new>

#include "ShowTracePort.h"
#include "../physical_effects/PresentationDiagnostics.h"

// Single control-task producer; HTTPS is an observer. Publication never waits
// for a slow reader. Journal evidence remains in PersistentCommandJournal.
class ShowTraceMailbox final : public ShowTraceReadPort {
 public:
  bool Publish(const PresentationDiagnostics& diagnostics, uint64_t nowMs) {
    std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
    if (!lock.owns_lock()) {
      skipped_.fetch_add(1, std::memory_order_relaxed);
      return false;
    }
    snapshot_ = diagnostics;
    publishedAtMs_ = nowMs;
    published_ = true;
    return true;
  }

  std::string ReadTrace(uint64_t nowMs) override {
    // The HTTP task owns allocation/serialization. Keep the ring off its stack.
    std::unique_ptr<PresentationDiagnostics> copy(new (std::nothrow) PresentationDiagnostics);
    if (!copy) {
      return {};
    }
    uint64_t publishedAt;
    {
      std::lock_guard<std::mutex> lock(mutex_);
      if (!published_) {
        return {};
      }
      *copy = snapshot_;
      publishedAt = publishedAtMs_;
    }
    auto json = ExportPresentationTrace(*copy);
    if (json.empty() || json.back() != '}') {
      return {};
    }
    json.pop_back();
    const bool stale = nowMs < publishedAt || nowMs - publishedAt > 2000;
    json += ",\"observerPublishedAtMs\":\"" + std::to_string(publishedAt) +
        "\",\"observerSkippedSnapshots\":" +
        std::to_string(skipped_.load(std::memory_order_relaxed)) +
        ",\"observerStale\":" + (stale ? "true}" : "false}");
    return json;
  }

 private:
  std::mutex mutex_;
  PresentationDiagnostics snapshot_;
  uint64_t publishedAtMs_ = 0;
  bool published_ = false;
  std::atomic<uint32_t> skipped_{0};
};

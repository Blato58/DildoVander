#pragma once

#include <algorithm>
#include <cstdint>
#include <mutex>
#include <string>
#include <utility>
#include <vector>
#include "../device_control/CommandJournal.h"

struct KeepsakeOutcome {
  std::string operationId, outcomeId, proposalId, physicalOperationId, bootId;
  uint64_t generation = 0, deviceRevision = 0, visibilityEpoch = 0;
  uint64_t retryAtMs = 0;
  uint32_t retryDelayMs = 1000;
  uint8_t compartment = 0;
};

enum class KeepsakeQueueResult : uint8_t { Rejected, VolatilePendingPersistence, AlreadyQueued };

struct KeepsakeDisposition {
  std::string operationId, outcomeId, reason;
};

struct KeepsakeOutboxState {
  std::vector<KeepsakeOutcome> pending;
  std::vector<KeepsakeDisposition> quarantined;
  std::vector<KeepsakeOutcome> prepared;
};

inline bool PermanentKeepsakeFailure(int status, const std::string& code) {
  return (status == 410 && code == "outcome_deleted")
      || (status == 409 && (code == "outcome_not_eligible" || code == "outcome_snapshot_conflict" || code == "outcome_conflict"
          || code == "operator_restore_required" || code == "idempotency_conflict"))
      || (status == 404 && code == "keepsake_not_found");
}

struct LocalKeepsakeStatus {
  std::string outcomeId, physicalOperationId, url;
  uint64_t sessionEpoch = 0, expiresAtEpochMs = 0;
};

// Completed physical facts only. Publication cannot trigger a physical action.
class KeepsakeOutbox final {
 public:
  static constexpr size_t Capacity = 8;
  static constexpr size_t QuarantineCapacity = 32;

  bool Prepare(const KeepsakeOutcome& outcome) {
    std::lock_guard<std::mutex> lock(mutex_);
    for (const auto& item : quarantined_) if (item.operationId == outcome.operationId) return false;
    for (const auto& item : prepared_) if (item.operationId == outcome.operationId)
      return item.outcomeId == outcome.outcomeId && item.physicalOperationId == outcome.physicalOperationId;
    if (Occupied() >= Capacity || quarantined_.size() >= QuarantineCapacity) return false;
    prepared_.push_back(outcome);
    return true;
  }

  void RemovePrepared(const std::string& outcomeId) {
    std::lock_guard<std::mutex> lock(mutex_);
    prepared_.erase(std::remove_if(prepared_.begin(), prepared_.end(), [&](const KeepsakeOutcome& item) {
      return item.outcomeId == outcomeId;
    }), prepared_.end());
  }

  KeepsakeQueueResult Queue(KeepsakeOutcome outcome, bool visible = true) {
    std::lock_guard<std::mutex> lock(mutex_);
    if (outcome.operationId.empty() || outcome.outcomeId.empty()) return KeepsakeQueueResult::Rejected;
    for (const auto& item : pending_) {
      if (item.operationId == outcome.operationId) return item.outcomeId == outcome.outcomeId
          ? KeepsakeQueueResult::AlreadyQueued : KeepsakeQueueResult::Rejected;
    }
    for (const auto& item : quarantined_) if (item.operationId == outcome.operationId) return KeepsakeQueueResult::Rejected;
    bool prepared = false;
    for (const auto& item : prepared_) prepared |= item.outcomeId == outcome.outcomeId;
    if ((!prepared && Occupied() >= Capacity)
        || pending_.size() >= Capacity || quarantined_.size() >= QuarantineCapacity) return KeepsakeQueueResult::Rejected;
    outcome.visibilityEpoch = visible ? visibilityEpoch_ : 0;
    pending_.push_back(std::move(outcome));
    return KeepsakeQueueResult::VolatilePendingPersistence;
  }

  std::vector<KeepsakeOutcome> Snapshot() const {
    std::lock_guard<std::mutex> lock(mutex_);
    return pending_;
  }

  KeepsakeOutboxState State() const {
    std::lock_guard<std::mutex> lock(mutex_);
    return {pending_, quarantined_, prepared_};
  }

  bool NextReady(uint64_t nowMs, KeepsakeOutcome& outcome) const {
    std::lock_guard<std::mutex> lock(mutex_);
    for (const auto& item : pending_) {
      if (item.retryAtMs <= nowMs) { outcome = item; return true; }
    }
    return false;
  }

  void Defer(const std::string& outcomeId, uint64_t nowMs) {
    std::lock_guard<std::mutex> lock(mutex_);
    for (auto& item : pending_) if (item.outcomeId == outcomeId) {
      item.retryDelayMs = std::min<uint32_t>(item.retryDelayMs * 2, 30000);
      item.retryAtMs = nowMs + item.retryDelayMs;
      return;
    }
  }

  bool RecordDisposition(const KeepsakeOutcome& outcome, const std::string& reason) {
    std::lock_guard<std::mutex> lock(mutex_);
    for (const auto& entry : quarantined_) if (entry.outcomeId == outcome.outcomeId) return true;
    if (quarantined_.size() >= QuarantineCapacity) return false;
    quarantined_.push_back({outcome.operationId, outcome.outcomeId, reason});
    return true;
  }

  bool HasDisposition(const std::string& outcomeId) const {
    std::lock_guard<std::mutex> lock(mutex_);
    for (const auto& entry : quarantined_) if (entry.outcomeId == outcomeId) return true;
    return false;
  }

  bool Restore(std::vector<KeepsakeOutcome> entries, std::vector<KeepsakeDisposition> dispositions = {},
               std::vector<KeepsakeOutcome> prepared = {}) {
    std::lock_guard<std::mutex> lock(mutex_);
    if (!pending_.empty() || entries.size() > Capacity || prepared.size() > Capacity
        || dispositions.size() > QuarantineCapacity) return false;
    for (size_t i = 0; i < entries.size(); ++i) {
      for (size_t j = 0; j < i; ++j) {
        if (entries[i].operationId == entries[j].operationId || entries[i].outcomeId == entries[j].outcomeId) return false;
      }
      entries[i].visibilityEpoch = 0;
    }
    for (size_t i = 0; i < dispositions.size(); ++i) {
      for (size_t j = 0; j < i; ++j) if (dispositions[i].outcomeId == dispositions[j].outcomeId
          || dispositions[i].operationId == dispositions[j].operationId) return false;
    }
    for (size_t i = 0; i < prepared.size(); ++i) {
      for (size_t j = 0; j < i; ++j) if (prepared[i].outcomeId == prepared[j].outcomeId
          || prepared[i].operationId == prepared[j].operationId) return false;
      for (const auto& pending : entries) if (pending.outcomeId == prepared[i].outcomeId
          && (pending.operationId != prepared[i].operationId || pending.physicalOperationId != prepared[i].physicalOperationId
              || pending.proposalId != prepared[i].proposalId || pending.bootId != prepared[i].bootId
              || pending.generation != prepared[i].generation || pending.deviceRevision != prepared[i].deviceRevision
              || pending.compartment != prepared[i].compartment)) return false;
    }
    // A persisted disposition fences replay even if power failed before queue removal.
    entries.erase(std::remove_if(entries.begin(), entries.end(), [&](const KeepsakeOutcome& item) {
      for (const auto& disposition : dispositions) if (disposition.outcomeId == item.outcomeId) return true;
      return false;
    }), entries.end());
    prepared.erase(std::remove_if(prepared.begin(), prepared.end(), [&](const KeepsakeOutcome& item) {
      for (const auto& disposition : dispositions) if (disposition.outcomeId == item.outcomeId) return true;
      return false;
    }), prepared.end());
    pending_ = std::move(entries);
    quarantined_ = std::move(dispositions);
    prepared_ = std::move(prepared);
    return Occupied() <= Capacity;
  }

  bool RecoverPrepared(const CommandJournal& journal) {
    const auto saved = State();
    for (const auto& outcome : saved.prepared) {
      if (journal.WasTimedPresentationCompleted(outcome.physicalOperationId, outcome.compartment)) {
        if (Queue(outcome, false) == KeepsakeQueueResult::Rejected) return false;
      } else if (!RecordDisposition(outcome, "physical_outcome_unknown")) {
        return false;
      }
      RemovePrepared(outcome.outcomeId);
    }
    return true;
  }

  void Complete(const std::string& outcomeId) {
    std::lock_guard<std::mutex> lock(mutex_);
    pending_.erase(std::remove_if(pending_.begin(), pending_.end(), [&](const KeepsakeOutcome& item) {
      return item.outcomeId == outcomeId;
    }), pending_.end());
  }

  void Hide() {
    std::lock_guard<std::mutex> lock(mutex_);
    ++visibilityEpoch_;
    published_ = {};
  }

  bool Publish(const KeepsakeOutcome& outcome, LocalKeepsakeStatus status) {
    std::lock_guard<std::mutex> lock(mutex_);
    if (outcome.visibilityEpoch == 0 || outcome.visibilityEpoch != visibilityEpoch_
        || status.outcomeId != outcome.outcomeId || status.physicalOperationId != outcome.physicalOperationId
        || status.sessionEpoch != outcome.generation) return false;
    published_ = std::move(status);
    return true;
  }

  LocalKeepsakeStatus Read(uint64_t generation, uint64_t nowEpochMs) const {
    std::lock_guard<std::mutex> lock(mutex_);
    if (published_.sessionEpoch != generation || published_.expiresAtEpochMs <= nowEpochMs) return {};
    return published_;
  }

 private:
  size_t Occupied() const {
    size_t count = pending_.size();
    for (const auto& prepared : prepared_) {
      bool pending = false;
      for (const auto& item : pending_) pending |= item.outcomeId == prepared.outcomeId;
      if (!pending) ++count;
    }
    return count;
  }
  mutable std::mutex mutex_;
  std::vector<KeepsakeOutcome> pending_;
  std::vector<KeepsakeDisposition> quarantined_;
  std::vector<KeepsakeOutcome> prepared_;
  LocalKeepsakeStatus published_;
  uint64_t visibilityEpoch_ = 1;
};

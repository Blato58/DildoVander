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
  uint64_t generation = 0, deviceRevision = 0;
  uint64_t retryAtMs = 0;
  uint32_t retryDelayMs = 1000;
  uint8_t compartment = 0;
};

enum class KeepsakeQueueResult : uint8_t { Rejected, VolatilePendingPersistence, AlreadyQueued };

struct KeepsakeDisposition {
  std::string operationId, outcomeId, reason;
};

struct KeepsakeRetirement {
  KeepsakeOutcome outcome;
  std::string reason;
};

inline bool SameKeepsakeCandidate(const KeepsakeOutcome& left, const KeepsakeOutcome& right) {
  return left.operationId == right.operationId && left.outcomeId == right.outcomeId
      && left.proposalId == right.proposalId && left.physicalOperationId == right.physicalOperationId
      && left.bootId == right.bootId && left.generation == right.generation
      && left.deviceRevision == right.deviceRevision && left.compartment == right.compartment;
}

inline bool PreparationDisposition(const std::string& reason) {
  return reason == "proposal_store_rejected" || reason == "proposal_stale_or_expired"
      || reason == "proposal_stopped" || reason == "proposal_cancelled" || reason == "presentation_fault"
      || reason == "proposal_start_rejected" || reason == "conversation_closed" || reason == "completion_unconfirmed";
}

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

  // Control/network producers only signal exact technical identity. Persistence
  // and slot release are performed by the notifier worker, never the control loop.
  bool RequestRetirement(const KeepsakeOutcome& outcome, const std::string& reason) {
    std::lock_guard<std::mutex> lock(mutex_);
    for (const auto& prepared : prepared_) {
      if (!SameKeepsakeCandidate(prepared, outcome)) continue;
      for (const auto& request : retirements_) if (request.outcome.outcomeId == outcome.outcomeId) return true;
      if (retirements_.size() >= Capacity) return false;
      retirements_.push_back({outcome, reason});
      return true;
    }
    return false;
  }

  bool HasRetirements() const {
    std::lock_guard<std::mutex> lock(mutex_);
    for (const auto& request : retirements_) if (request.reason != "outcome_delivered") return true;
    return false;
  }

  bool RequestDeliveryAcknowledgement(const KeepsakeOutcome& outcome) {
    std::lock_guard<std::mutex> lock(mutex_);
    for (const auto& pending : pending_) {
      if (!SameKeepsakeCandidate(pending, outcome)) continue;
      for (auto& request : retirements_) if (request.outcome.outcomeId == outcome.outcomeId) {
        request.reason = "outcome_delivered";
        return true;
      }
      if (retirements_.size() >= Capacity) return false;
      retirements_.push_back({outcome, "outcome_delivered"});
      return true;
    }
    return false;
  }

  template <typename PersistState>
  bool ProcessDeliveryAcknowledgements(PersistState persist) {
    const auto requests = RetirementSnapshot();
    for (const auto& request : requests) {
      if (request.reason != "outcome_delivered") continue;
      if (!RecordDisposition(request.outcome, request.reason) || !CommitRetirement(request.outcome, persist)) return false;
    }
    return true;
  }

  template <typename PersistState>
  bool ProcessRetirements(const CommandJournal& journal, PersistState persist) {
    const auto requests = RetirementSnapshot();
    for (const auto& request : requests) {
      if (request.reason == "outcome_delivered") continue;
      const auto& outcome = request.outcome;
      // A validated historical completion wins over a late cancellation signal.
      if (journal.WasTimedPresentationCompleted(outcome.physicalOperationId, outcome.compartment)) {
        if (Queue(outcome) == KeepsakeQueueResult::Rejected) return false;
      } else if (!RecordDisposition(outcome, request.reason)) {
        return false;
      }
      if (!CommitRetirement(outcome, persist)) return false;
    }
    return true;
  }

  void CompactPreparationDispositions() {
    std::lock_guard<std::mutex> lock(mutex_);
    quarantined_.erase(std::remove_if(quarantined_.begin(), quarantined_.end(), [&](const KeepsakeDisposition& item) {
      if (!PreparationDisposition(item.reason) && item.reason != "outcome_delivered") return false;
      for (const auto& prepared : prepared_) if (prepared.outcomeId == item.outcomeId) return false;
      for (const auto& pending : pending_) if (pending.outcomeId == item.outcomeId) return false;
      return true;
    }), quarantined_.end());
  }

  KeepsakeQueueResult Queue(KeepsakeOutcome outcome) {
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
      bool acknowledged = false;
      for (const auto& request : retirements_)
        acknowledged |= request.reason == "outcome_delivered" && request.outcome.outcomeId == item.outcomeId;
      if (acknowledged) continue;
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
        if (Queue(outcome) == KeepsakeQueueResult::Rejected) return false;
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

 private:
  std::vector<KeepsakeRetirement> RetirementSnapshot() const {
    std::lock_guard<std::mutex> lock(mutex_);
    return retirements_;
  }

  template <typename PersistState>
  bool CommitRetirement(const KeepsakeOutcome& outcome, PersistState persist) {
    // Commit the ACK/disposition before removal. Until removal is committed the
    // RAM mailbox keeps capacity occupied, including across failed NVS writes.
    if (!persist(State())) return false;
    RemovePrepared(outcome.outcomeId);
    if (HasDisposition(outcome.outcomeId)) Complete(outcome.outcomeId);
    if (!persist(State())) return false;
    {
      std::lock_guard<std::mutex> lock(mutex_);
      retirements_.erase(std::remove_if(retirements_.begin(), retirements_.end(), [&](const KeepsakeRetirement& item) {
        return item.outcome.outcomeId == outcome.outcomeId;
      }), retirements_.end());
    }
    // The server owns delivered content. Only temporary local ACK/preparation
    // fences are compacted after durable removal; terminal failures remain.
    CompactPreparationDispositions();
    return persist(State());
  }
  size_t Occupied() const {
    size_t count = pending_.size();
    for (const auto& prepared : prepared_) {
      bool pending = false;
      for (const auto& item : pending_) pending |= item.outcomeId == prepared.outcomeId;
      if (!pending) ++count;
    }
    // A failed removal commit still owns capacity through its retirement mailbox.
    for (const auto& request : retirements_) {
      bool occupied = false;
      for (const auto& item : pending_) occupied |= item.outcomeId == request.outcome.outcomeId;
      for (const auto& item : prepared_) occupied |= item.outcomeId == request.outcome.outcomeId;
      if (!occupied) ++count;
    }
    return count;
  }
  mutable std::mutex mutex_;
  std::vector<KeepsakeOutcome> pending_;
  std::vector<KeepsakeDisposition> quarantined_;
  std::vector<KeepsakeOutcome> prepared_;
  std::vector<KeepsakeRetirement> retirements_;
};

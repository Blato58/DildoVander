#include "KeepsakeEvidenceBridge.h"

#include <algorithm>
#include <climits>
#include <cstring>
#include <limits>

namespace {
bool Uuid(const std::array<char, 37>& value) {
  if (value.back()) return false;
  for (size_t index = 0; index < 36; ++index) {
    const char character = JournalIdentityLower(value[index]);
    if (index == 8 || index == 13 || index == 18 || index == 23) {
      if (character != '-') return false;
    } else if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))) return false;
  }
  return true;
}
bool Copy(const std::string& source, std::array<char, 37>& target) {
  if (source.size() >= target.size()) return false;
  std::copy(source.begin(), source.end(), target.begin());
  return true;
}
bool PermanentRetirement(const char* reason) {
  static const char* const reasons[] = {"outcome_deleted", "outcome_not_eligible", "outcome_snapshot_conflict",
      "outcome_conflict", "operator_restore_required", "idempotency_conflict", "keepsake_not_found"};
  for (const char* candidate : reasons) if (std::strcmp(reason, candidate) == 0) return true;
  return false;
}
}

KeepsakeEvidenceBridge::KeepsakeEvidenceBridge(EvidenceStorageOwner& owner, JournalAdmissionGate& gate)
    : owner_(owner), gate_(gate) {}

bool KeepsakeEvidenceBridge::Valid(const Outcome& outcome) {
  return Uuid(outcome.operationId) && Uuid(outcome.outcomeId) && Uuid(outcome.proposalId)
      && Uuid(outcome.physicalOperationId) && Uuid(outcome.bootId)
      && outcome.generation <= INT32_MAX && outcome.deviceRevision <= INT32_MAX
      && outcome.compartment >= 1 && outcome.compartment <= 6
      && outcome.operationEpoch && outcome.physicalFingerprint;
}
bool KeepsakeEvidenceBridge::SameIdentity(const Outcome& left, const Outcome& right) {
  return left.operationId == right.operationId && left.outcomeId == right.outcomeId
      && left.proposalId == right.proposalId && left.physicalOperationId == right.physicalOperationId
      && left.bootId == right.bootId && left.generation == right.generation
      && left.deviceRevision == right.deviceRevision && left.compartment == right.compartment
      && left.operationEpoch == right.operationEpoch && left.physicalFingerprint == right.physicalFingerprint;
}
bool KeepsakeEvidenceBridge::Same(const Outcome& left, const Outcome& right) {
  return SameIdentity(left, right) && left.retryAtMs == right.retryAtMs && left.retryDelayMs == right.retryDelayMs;
}
bool KeepsakeEvidenceBridge::CopyReason(const char* reason, std::array<char, 65>& target) {
  if (!reason) return false;
  size_t length = 0;
  while (length < target.size() && reason[length]) ++length;
  if (!length || length == target.size()) return false;
  static const char* const allowed[] = {"proposal_store_rejected", "proposal_stale_or_expired", "proposal_stopped",
      "proposal_cancelled", "presentation_fault", "proposal_start_rejected", "conversation_closed",
      "completion_unconfirmed", "physical_outcome_unknown", "outcome_deleted", "outcome_not_eligible",
      "outcome_snapshot_conflict", "outcome_conflict", "operator_restore_required", "idempotency_conflict", "keepsake_not_found"};
  for (const char* candidate : allowed) {
    if (std::strcmp(reason, candidate)) continue;
    target.fill(0);
    std::copy(reason, reason + length, target.begin());
    return true;
  }
  return false;
}
bool KeepsakeEvidenceBridge::Fixed(const KeepsakeOutcome& source, Outcome& target) {
  Outcome result;
  if (!Copy(source.operationId, result.operationId) || !Copy(source.outcomeId, result.outcomeId)
      || !Copy(source.proposalId, result.proposalId) || !Copy(source.physicalOperationId, result.physicalOperationId)
      || !Copy(source.bootId, result.bootId)) return false;
  result.generation = source.generation; result.deviceRevision = source.deviceRevision;
  result.retryAtMs = source.retryAtMs; result.retryDelayMs = source.retryDelayMs;
  result.compartment = source.compartment; result.operationEpoch = source.operationEpoch;
  result.physicalFingerprint = source.physicalFingerprint;
  target = result;
  return true;
}
bool KeepsakeEvidenceBridge::Matches(const KeepsakeOutcome& source, const Outcome& target) {
  Outcome fixed;
  return Fixed(source, fixed) && SameIdentity(fixed, target);
}
bool KeepsakeEvidenceBridge::Disposed(const KeepsakeOutboxState& state, const Slot& slot, const char* reason) {
  for (const auto& item : state.quarantined) {
    if (item.reason == reason && item.operationId == slot.outcome.operationId.data()
        && item.outcomeId == slot.outcome.outcomeId.data() && item.operationEpoch == slot.outcome.operationEpoch
        && item.physicalOperationId == slot.outcome.physicalOperationId.data()
        && item.compartment == slot.outcome.compartment && item.physicalFingerprint
        && item.physicalFingerprint == slot.outcome.physicalFingerprint) return true;
  }
  return false;
}
KeepsakeEvidenceBridge::Slot* KeepsakeEvidenceBridge::Find(Handle handle) {
  return handle.generation && handle.slot < Capacity && slots_[handle.slot].occupied
      && slots_[handle.slot].generation == handle.generation ? &slots_[handle.slot] : nullptr;
}
const KeepsakeEvidenceBridge::Slot* KeepsakeEvidenceBridge::Find(Handle handle) const {
  return handle.generation && handle.slot < Capacity && slots_[handle.slot].occupied
      && slots_[handle.slot].generation == handle.generation ? &slots_[handle.slot] : nullptr;
}
bool KeepsakeEvidenceBridge::InFlight(Handle handle) const {
  return flight_.token.id && flight_.handle.slot == handle.slot && flight_.handle.generation == handle.generation;
}
void KeepsakeEvidenceBridge::Clear(Slot& slot) {
  const auto generation = slot.generation;
  slot = {}; slot.generation = generation;
}
void KeepsakeEvidenceBridge::Poison() {
  poisoned_ = true;
  for (auto& slot : slots_) if (slot.occupied) {
    slot.prepare = PrepareStatus::Unknown; slot.revoked = true;
  }
}

KeepsakeEvidenceBridge::PrepareResult KeepsakeEvidenceBridge::TryPrepare(const Outcome& outcome, Handle& handle) {
  std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
  if (!lock) return PrepareResult::Busy;
  if (handle.generation) {
    const auto slot = Find(handle);
    return slot && Same(slot->outcome, outcome) ? PrepareResult::Pending : PrepareResult::Refused;
  }
  if (!bootstrapped_ || poisoned_ || !gate_.AllowsAdmission() || !Valid(outcome)) return PrepareResult::Refused;
  for (const auto& slot : slots_) if (slot.occupied
      && (slot.outcome.outcomeId == outcome.outcomeId || slot.outcome.operationId == outcome.operationId
          || (slot.outcome.operationEpoch == outcome.operationEpoch
              && slot.outcome.physicalOperationId == outcome.physicalOperationId))) return PrepareResult::Refused;
  for (size_t index = 0; index < Capacity; ++index) {
    auto& slot = slots_[index];
    if (slot.occupied || slot.generation == std::numeric_limits<uint64_t>::max()) continue;
    ++slot.generation; slot.occupied = true; slot.outcome = outcome; slot.prepareIntent = true;
    slot.retryAtMs = outcome.retryAtMs; slot.retryDelayMs = outcome.retryDelayMs;
    handle.slot = static_cast<uint8_t>(index); handle.generation = slot.generation;
    return PrepareResult::Pending;
  }
  return PrepareResult::Busy;
}
KeepsakeEvidenceBridge::PrepareStatus KeepsakeEvidenceBridge::PollPrepare(Handle handle) const {
  std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
  if (!lock) return PrepareStatus::Pending;
  const auto slot = Find(handle);
  return slot ? slot->prepare : PrepareStatus::Refused;
}
KeepsakeEvidenceBridge::Transfer KeepsakeEvidenceBridge::QueueComplete(Handle handle) {
  std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
  if (!lock) return Transfer::Busy;
  auto slot = Find(handle);
  if (!slot || poisoned_ || slot->conservative) return Transfer::Rejected;
  if (slot->terminal) return slot->completeRetained ? Transfer::AlreadyRetained : Transfer::Rejected;
  slot->terminalIntentRetained = slot->completeRetained = true;
  if (slot->completeIntent || slot->publishedPending) return Transfer::AlreadyRetained;
  slot->completeIntent = true;
  return Transfer::RetainedVolatile;
}
KeepsakeEvidenceBridge::Transfer KeepsakeEvidenceBridge::QueueRetire(Handle handle, const char* reason) {
  std::array<char, 65> fixed{};
  if (!CopyReason(reason, fixed)) return Transfer::Rejected;
  std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
  if (!lock) return Transfer::Busy;
  auto slot = Find(handle);
  if (!slot || poisoned_ || slot->conservative) return Transfer::Rejected;
  if (slot->reason.front()) {
    if (slot->reason != fixed) return Transfer::Rejected;
    slot->terminalIntentRetained = true;
    return Transfer::AlreadyRetained;
  }
  slot->terminalIntentRetained = true;
  // An already-published real completion wins over preparation cancellation.
  // Retain the caller's handoff, but do not freeze a cancellation reason that
  // would prevent a later, independently observed permanent delivery failure.
  if (slot->publishedPending && !PermanentRetirement(reason)) return Transfer::AlreadyRetained;
  slot->reason = fixed; slot->retireIntent = true; slot->revoked = true;
  if (slot->terminal) { slot->retireIntent = false; return Transfer::RetainedVolatile; }
  if (slot->prepare == PrepareStatus::DurablePrepared) slot->prepare = PrepareStatus::Refused;
  if (slot->prepareIntent && !InFlight(handle) && !slot->completeIntent) {
    slot->prepareIntent = slot->retireIntent = false;
    slot->prepare = PrepareStatus::CancelledBeforeWrite; slot->terminal = true;
  }
  return Transfer::RetainedVolatile;
}
bool KeepsakeEvidenceBridge::TransferToNetwork(Handle handle) {
  std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
  if (!lock || poisoned_) return false;
  auto slot = Find(handle);
  if (!slot || slot->conservative || !slot->terminalIntentRetained) return false;
  slot->networkOwned = true;
  return true;
}
bool KeepsakeEvidenceBridge::Release(Handle handle) {
  std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
  if (!lock || poisoned_) return false;
  auto slot = Find(handle);
  if (!slot || !slot->terminal || InFlight(handle) || slot->publishedPending || slot->publishedPrepared) return false;
  Clear(*slot);
  return true;
}

bool KeepsakeEvidenceBridge::Import(const KeepsakeOutboxState& state) {
  const auto import = [&](const KeepsakeOutcome& outcome) {
    Outcome fixed;
    if (!Fixed(outcome, fixed)) return false;
    for (const auto& slot : slots_) if (slot.occupied && SameIdentity(slot.outcome, fixed)) return true;
    for (auto& slot : slots_) {
      if (slot.occupied || slot.generation == std::numeric_limits<uint64_t>::max()) continue;
      ++slot.generation; slot.occupied = true; slot.networkOwned = true; slot.outcome = fixed;
      slot.retryAtMs = fixed.retryAtMs; slot.retryDelayMs = fixed.retryDelayMs;
      slot.conservative = !Valid(fixed); slot.revoked = true;
      slot.prepare = PrepareStatus::CancelledBeforeWrite;
      return true;
    }
    return false;
  };
  for (const auto& outcome : state.pending) if (!import(outcome)) return false;
  for (const auto& outcome : state.prepared) if (!import(outcome)) return false;
  Refresh(state);
  for (auto& slot : slots_) if (slot.occupied && !slot.conservative) {
    // A lost removal acknowledgement can leave the live row together with its
    // durable disposition. Resume that exact typed cleanup without another HTTP
    // send. Legacy rows cannot acquire this binding from ledger metadata.
    for (const auto& disposition : state.quarantined) {
      if (!Disposed(state, slot, disposition.reason.c_str())) continue;
      if (disposition.reason == "outcome_delivered" && slot.publishedPending) slot.ackIntent = true;
      else if (CopyReason(disposition.reason.c_str(), slot.reason)) slot.retireIntent = true;
      else slot.conservative = true;
      break;
    }
  }
  return true;
}
void KeepsakeEvidenceBridge::Refresh(const KeepsakeOutboxState& state) {
  for (auto& slot : slots_) if (slot.occupied) {
    slot.publishedPending = false; slot.publishedPrepared = false;
    for (const auto& item : state.pending) slot.publishedPending |= Matches(item, slot.outcome);
    for (const auto& item : state.prepared) slot.publishedPrepared |= Matches(item, slot.outcome);
    if (slot.publishedPending) slot.completeIntent = false;
  }
}
bool KeepsakeEvidenceBridge::Bootstrap() {
  KeepsakeOutboxState state;
  if (!owner_.IsHealthy() || !owner_.TryReadOnline(state)) return false;
  std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
  if (!lock || poisoned_) return false;
  if (bootstrapped_) return true;
  if (!Import(state)) { Poison(); return false; }
  bootstrapped_ = true;
  return true;
}
bool KeepsakeEvidenceBridge::Adopt(const EvidenceStorageOwner::Completion& result, const KeepsakeOutboxState& state) {
  auto slot = Find(flight_.handle);
  if (!slot) { Poison(); return true; }
  if (result.status == EvidenceStorageOwner::Status::Unknown) { Poison(); return true; }
  const bool success = result.status == EvidenceStorageOwner::Status::Succeeded;
  if (flight_.operation == Operation::OnlinePrepare) {
    slot->prepareIntent = false;
    if (slot->publishedPrepared || slot->publishedPending) {
      // Cancellation may be observed after the write, so even a refused or
      // cancelled result must adopt an exact row that reached the publication.
      slot->revoked |= !success || !result.current;
      slot->prepare = slot->revoked ? PrepareStatus::Refused : PrepareStatus::DurablePrepared;
      // Late success always transfers into retirement, even if the caller has
      // already discarded live presentation authority after Stop.
      if (slot->revoked && !slot->retireIntent) {
        CopyReason("proposal_cancelled", slot->reason); slot->retireIntent = true;
        slot->terminalIntentRetained = true;
      }
    } else {
      if (success) { Poison(); return true; }
      slot->prepare = result.status == EvidenceStorageOwner::Status::CancelledBeforeWrite
          ? PrepareStatus::CancelledBeforeWrite : PrepareStatus::Refused;
      slot->terminal = !slot->publishedPrepared && !slot->publishedPending;
      if (slot->terminal) slot->completeIntent = slot->retireIntent = slot->ackIntent = false;
    }
  } else if (flight_.operation == Operation::OnlineComplete) {
    if (success && !slot->publishedPending) { Poison(); return true; }
    if (success) {
      slot->completeIntent = false;
      if (slot->reason.front() && !PermanentRetirement(slot->reason.data())) {
        slot->reason = {}; slot->retireIntent = false;
      }
    }
  } else if (flight_.operation == Operation::OnlineRetire || flight_.operation == Operation::OnlineAck) {
    const bool ack = flight_.operation == Operation::OnlineAck;
    const char* reason = ack ? "outcome_delivered" : slot->reason.data();
    if (success) {
      // Retirement may have promoted a real timed completion. A success result
      // alone never frees the slot or hides the newly published delivery.
      if (!slot->publishedPending && !slot->publishedPrepared && Disposed(state, *slot, reason)) {
        slot->terminal = true; slot->completeIntent = slot->retireIntent = slot->ackIntent = false;
      } else if (!ack && slot->publishedPending && !slot->publishedPrepared) {
        slot->completeIntent = false;
        if (!PermanentRetirement(slot->reason.data())) {
          slot->retireIntent = false; slot->reason = {};
        }
      } else { Poison(); return true; }
    } else if (!ack && slot->publishedPending
        && (std::strncmp(reason, "proposal_", 9) == 0 || std::strcmp(reason, "presentation_fault") == 0
            || std::strcmp(reason, "conversation_closed") == 0 || std::strcmp(reason, "completion_unconfirmed") == 0)) {
      // A complete queued before cancellation has already won. The source
      // correctly refuses preparation-only retirement of a pending obligation.
      slot->retireIntent = false;
      slot->reason = {};
    }
  }
  return true;
}
bool KeepsakeEvidenceBridge::Submit() {
  for (size_t count = 0; count < Capacity; ++count) {
    const size_t index = (next_ + count) % Capacity;
    auto& slot = slots_[index];
    if (!slot.occupied || slot.terminal || slot.conservative) continue;
    EvidenceStorageOwner::Request request;
    request.online = slot.outcome;
    if (slot.prepareIntent) request.operation = Operation::OnlinePrepare;
    else if (slot.ackIntent) request.operation = Operation::OnlineAck;
    else if (slot.retireIntent) { request.operation = Operation::OnlineRetire; request.reason = slot.reason; }
    else if (slot.completeIntent) request.operation = Operation::OnlineComplete;
    else continue;
    EvidenceStorageOwner::Token token;
    if (!owner_.TrySubmit(request, token)) return false;
    flight_.token = token; flight_.handle.slot = static_cast<uint8_t>(index);
    flight_.handle.generation = slot.generation; flight_.operation = request.operation;
    next_ = (index + 1) % Capacity;
    return true;
  }
  return false;
}
bool KeepsakeEvidenceBridge::Pump() {
  // The sole network task owns flight_; control only observes it under mutex_.
  if (flight_.token.id && flight_.adopted) {
    if (!owner_.ReleaseCompletion(flight_.token)) return false;
    std::lock_guard<std::mutex> lock(mutex_);
    auto slot = Find(flight_.handle);
    if (slot && slot->terminal && slot->networkOwned && !poisoned_) Clear(*slot);
    flight_ = {};
  }
  EvidenceStorageOwner::Completion result;
  const bool completed = flight_.token.id && owner_.TryReadCompletion(flight_.token, result);
  KeepsakeOutboxState state;
  uint64_t generation = 0;
  const bool published = owner_.TryReadOnline(state, &generation);
  std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
  if (!lock) return false;
  if (!bootstrapped_) return false;
  if (!owner_.IsHealthy()) Poison();
  if (published) Refresh(state);
  if (completed) {
    if (result.status != EvidenceStorageOwner::Status::Unknown
        && (!published || generation < result.onlineGeneration)) return false;
    Adopt(result, state); flight_.adopted = true;
    return true;
  }
  if (poisoned_ || flight_.token.id) return false;
  for (auto& slot : slots_) if (slot.occupied && slot.networkOwned && slot.terminal
      && !slot.publishedPending && !slot.publishedPrepared) Clear(slot);
  return Submit();
}
bool KeepsakeEvidenceBridge::LookupPending(uint64_t nowMs, Pending& pending) {
  KeepsakeOutboxState state;
  uint64_t generation = 0;
  if (!owner_.IsHealthy() || !owner_.TryReadOnline(state, &generation)) return false;
  std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
  if (!lock || !bootstrapped_ || poisoned_) return false;
  Refresh(state);
  for (const auto& item : state.pending) {
    for (size_t index = 0; index < Capacity; ++index) {
      const auto& slot = slots_[index];
      if (!slot.occupied || slot.conservative || slot.terminal || slot.ackIntent || slot.retireIntent
          || slot.retryAtMs > nowMs || !Matches(item, slot.outcome)) continue;
      pending.handle.slot = static_cast<uint8_t>(index); pending.handle.generation = slot.generation;
      if (!Fixed(item, pending.outcome)) return false;
      pending.outcome.retryAtMs = slot.retryAtMs; pending.outcome.retryDelayMs = slot.retryDelayMs;
      pending.publicationGeneration = generation;
      return true;
    }
  }
  return false;
}
KeepsakeEvidenceBridge::Transfer KeepsakeEvidenceBridge::QueueValidatedAck(const Pending& acknowledged) {
  std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
  if (!lock) return Transfer::Busy;
  auto slot = Find(acknowledged.handle);
  if (!slot || poisoned_ || slot->conservative || slot->terminal || !slot->publishedPending
      || !acknowledged.publicationGeneration || !SameIdentity(slot->outcome, acknowledged.outcome)) return Transfer::Rejected;
  if (slot->ackIntent) return Transfer::AlreadyRetained;
  slot->ackIntent = true;
  return Transfer::RetainedVolatile;
}
KeepsakeEvidenceBridge::Transfer KeepsakeEvidenceBridge::QueueDeferred(const Pending& failed, uint64_t nowMs) {
  std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
  if (!lock) return Transfer::Busy;
  auto slot = Find(failed.handle);
  if (!slot || poisoned_ || slot->conservative || slot->terminal || !slot->publishedPending
      || !failed.publicationGeneration || !SameIdentity(slot->outcome, failed.outcome)) return Transfer::Rejected;
  if (slot->ackIntent || slot->retireIntent) return Transfer::AlreadyRetained;
  // A duplicate failure for an earlier HTTP selection cannot double the newer
  // backoff or shorten it. Only the selected schedule may advance itself.
  if (failed.outcome.retryAtMs != slot->retryAtMs || failed.outcome.retryDelayMs != slot->retryDelayMs)
    return Transfer::AlreadyRetained;
  const uint32_t previous = std::max<uint32_t>(1000, std::min<uint32_t>(slot->retryDelayMs, 30000));
  slot->retryDelayMs = std::min<uint32_t>(previous * 2, 30000);
  const uint64_t maximum = std::numeric_limits<uint64_t>::max();
  const uint64_t deadline = nowMs > maximum - slot->retryDelayMs ? maximum : nowMs + slot->retryDelayMs;
  slot->retryAtMs = std::max(slot->retryAtMs, deadline);
  return Transfer::RetainedVolatile;
}
bool KeepsakeEvidenceBridge::IsPoisoned() const {
  std::lock_guard<std::mutex> lock(mutex_);
  return poisoned_;
}
size_t KeepsakeEvidenceBridge::Occupied() const {
  std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
  if (!lock) return Capacity;
  size_t occupied = 0;
  for (const auto& slot : slots_) occupied += slot.occupied;
  return occupied;
}

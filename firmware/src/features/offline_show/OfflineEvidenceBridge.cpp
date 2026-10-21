#include "OfflineEvidenceBridge.h"

#include <limits>

namespace kufr { namespace offline {
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
bool SameOnline(const EvidenceStorageOwner::OnlineOutcome& left, const EvidenceStorageOwner::OnlineOutcome& right) {
  return left.operationId == right.operationId && left.outcomeId == right.outcomeId && left.proposalId == right.proposalId
      && left.physicalOperationId == right.physicalOperationId && left.bootId == right.bootId
      && left.generation == right.generation && left.deviceRevision == right.deviceRevision
      && left.retryAtMs == right.retryAtMs && left.retryDelayMs == right.retryDelayMs
      && left.compartment == right.compartment && left.operationEpoch == right.operationEpoch
      && left.physicalFingerprint == right.physicalFingerprint;
}
}
OfflineEvidenceBridge::OfflineEvidenceBridge(EvidenceStorageOwner& owner, JournalAdmissionGate& gate)
    : owner_(owner), gate_(gate) {}

bool OfflineEvidenceBridge::Valid(const Outcome& outcome) {
  return Uuid(outcome.operationId) && Uuid(outcome.bootId) && Uuid(outcome.localEveningId)
      && outcome.generation && outcome.operationEpoch && outcome.physicalFingerprint
      && outcome.slot >= 1 && outcome.slot <= 6
      && (!outcome.completed || outcome.completedAtMs >= outcome.startedAtMs);
}
bool OfflineEvidenceBridge::Empty(const Outcome& outcome) { return Same(outcome, Outcome{}); }
bool OfflineEvidenceBridge::SameIdentity(const Outcome& left, const Outcome& right) {
  return left.operationId == right.operationId && left.bootId == right.bootId && left.localEveningId == right.localEveningId
      && left.generation == right.generation && left.startedAtMs == right.startedAtMs && left.slot == right.slot
      && left.scripted == right.scripted && left.shortVariant == right.shortVariant
      && left.operationEpoch == right.operationEpoch && left.physicalFingerprint == right.physicalFingerprint
      && left.deviceRevision == right.deviceRevision;
}
bool OfflineEvidenceBridge::Same(const Outcome& left, const Outcome& right) {
  return SameIdentity(left, right) && left.completed == right.completed && left.completedAtMs == right.completedAtMs;
}
bool OfflineEvidenceBridge::SameRequest(const EvidenceStorageOwner::Request& left, const EvidenceStorageOwner::Request& right) {
  return left.operation == right.operation && SameOnline(left.online, right.online) && Same(left.offline, right.offline)
      && left.reason == right.reason && left.setQuiet == right.setQuiet && left.quiet == right.quiet
      && left.setAmbient == right.setAmbient && left.ambient == right.ambient
      && left.setEnabled == right.setEnabled && left.enabled == right.enabled
      && left.serverSettingsRevision == right.serverSettingsRevision && left.serverStopRevision == right.serverStopRevision
      && left.expectedStopRevision == right.expectedStopRevision && left.fenceStopRevision == right.fenceStopRevision;
}
bool OfflineEvidenceBridge::ValidSettings(Lane lane, const EvidenceStorageOwner::Request& request) {
  if (static_cast<size_t>(lane) >= 3) return false;
  if (lane == Lane::Enabled) return request.operation == Operation::OfflineEnabled && request.setEnabled
      && !request.setQuiet && !request.setAmbient && !request.serverSettingsRevision && !request.serverStopRevision;
  if (request.operation != Operation::OfflineSettings) return false;
  if (request.serverSettingsRevision && (!request.setQuiet || !request.setAmbient)) return false;
  if (lane == Lane::Stop) return request.serverStopRevision || (request.setQuiet && request.quiet
      && request.setAmbient && !request.ambient && request.setEnabled && !request.enabled);
  return !request.serverStopRevision && (request.setQuiet || request.setAmbient || request.setEnabled);
}
bool OfflineEvidenceBridge::MatchesSettings(const EvidenceStorageOwner::Request& request, const Outbox& state) {
  if (request.serverStopRevision) return state.serverStopRevision == request.serverStopRevision
      && state.quiet && !state.ambientEnabled && !state.offlineEnabled;
  return (!request.serverSettingsRevision || request.serverSettingsRevision == state.serverSettingsRevision)
      && (!request.setQuiet || request.quiet == state.quiet)
      && (!request.setAmbient || request.ambient == state.ambientEnabled)
      && (!request.setEnabled || request.enabled == state.offlineEnabled);
}
bool OfflineEvidenceBridge::Relaxing(const EvidenceStorageOwner::Request& request) {
  return !request.serverStopRevision && ((request.setQuiet && !request.quiet)
      || (request.setAmbient && request.ambient) || (request.setEnabled && request.enabled));
}
OfflineEvidenceBridge::Status OfflineEvidenceBridge::Convert(EvidenceStorageOwner::Status status) {
  switch (status) {
    case EvidenceStorageOwner::Status::Succeeded: return Status::Succeeded;
    case EvidenceStorageOwner::Status::Refused: return Status::Refused;
    case EvidenceStorageOwner::Status::Unknown: return Status::Unknown;
    case EvidenceStorageOwner::Status::CancelledBeforeWrite: return Status::CancelledBeforeWrite;
  }
  return Status::Unknown;
}
OfflineEvidenceBridge::Candidate* OfflineEvidenceBridge::Get(Handle handle) {
  return handle.generation && handle.slot < Capacity && candidates_[handle.slot].occupied
      && candidates_[handle.slot].generation == handle.generation ? &candidates_[handle.slot] : nullptr;
}
const OfflineEvidenceBridge::Candidate* OfflineEvidenceBridge::Get(Handle handle) const {
  return handle.generation && handle.slot < Capacity && candidates_[handle.slot].occupied
      && candidates_[handle.slot].generation == handle.generation ? &candidates_[handle.slot] : nullptr;
}
OfflineEvidenceBridge::SettingsIntent* OfflineEvidenceBridge::Get(IntentHandle handle) {
  const size_t lane = static_cast<size_t>(handle.lane);
  return handle.generation && lane < settings_.size() && handle.slot < 2 && settings_[lane][handle.slot].occupied
      && settings_[lane][handle.slot].generation == handle.generation ? &settings_[lane][handle.slot] : nullptr;
}
const OfflineEvidenceBridge::SettingsIntent* OfflineEvidenceBridge::Get(IntentHandle handle) const {
  const size_t lane = static_cast<size_t>(handle.lane);
  return handle.generation && lane < settings_.size() && handle.slot < 2 && settings_[lane][handle.slot].occupied
      && settings_[lane][handle.slot].generation == handle.generation ? &settings_[lane][handle.slot] : nullptr;
}
bool OfflineEvidenceBridge::InFlight(Handle handle) const {
  return flight_.token.id && !flight_.isSettings && flight_.candidate.slot == handle.slot
      && flight_.candidate.generation == handle.generation;
}
bool OfflineEvidenceBridge::InFlight(IntentHandle handle) const {
  return flight_.token.id && flight_.isSettings && flight_.settings.lane == handle.lane
      && flight_.settings.slot == handle.slot && flight_.settings.generation == handle.generation;
}
void OfflineEvidenceBridge::Poison() {
  poisoned_ = true;
  for (auto& candidate : candidates_) if (candidate.occupied) {
    candidate.revoked = true; candidate.reserve = ReserveStatus::Unknown;
    candidate.complete = candidate.ack = Status::Unknown;
  }
  for (auto& lane : settings_) for (auto& intent : lane) if (intent.occupied) intent.status = Status::Unknown;
}
bool OfflineEvidenceBridge::Refresh(const Outbox& state, uint64_t generation) {
  if (!generation || (publicationGeneration_ && generation < publicationGeneration_)) { Poison(); return false; }
  publication_ = state; publicationGeneration_ = generation;
  for (auto& candidate : candidates_) if (candidate.occupied) {
    candidate.present = false;
    for (const auto& record : state.records) {
      if (!SameIdentity(record, candidate.original)) continue;
      if (candidate.present) { Poison(); return false; }
      candidate.present = true; candidate.published = record;
    }
  }
  return true;
}
bool OfflineEvidenceBridge::Bootstrap() {
  if (poisoned_) return false;
  Outbox state; uint64_t generation = 0;
  if (!owner_.IsHealthy() || !owner_.TryReadOffline(state, &generation) || !generation) return false;
  if (bootstrapped_) return true;
  for (const auto& record : state.records) {
    if (Empty(record)) continue;
    for (auto& candidate : candidates_) {
      if (candidate.occupied) continue;
      ++candidate.generation; candidate.occupied = true; candidate.original = record;
      candidate.conservative = !Valid(record); candidate.revoked = true;
      candidate.reserve = ReserveStatus::Refused;
      candidate.completionCaptured = record.completed; candidate.completedAtMs = record.completedAtMs;
      if (record.completed) candidate.complete = Status::Succeeded;
      break;
    }
  }
  if (!Refresh(state, generation)) return false;
  bootstrapped_ = true;
  return true;
}
bool OfflineEvidenceBridge::Read(Outbox& outbox) const {
  if (!bootstrapped_ || poisoned_ || !owner_.IsHealthy()) return false;
  outbox = publication_;
  return true;
}
bool OfflineEvidenceBridge::Find(const Outcome& published, Handle& handle) const {
  if (!bootstrapped_ || poisoned_) return false;
  for (size_t index = 0; index < Capacity; ++index) {
    const auto& candidate = candidates_[index];
    if (!candidate.occupied || !candidate.present || !Same(candidate.published, published)) continue;
    handle.slot = static_cast<uint8_t>(index); handle.generation = candidate.generation;
    return true;
  }
  return false;
}
OfflineEvidenceBridge::Transfer OfflineEvidenceBridge::Reserve(const Outcome& original, Handle& handle) {
  if (handle.generation) {
    const auto candidate = Get(handle);
    return candidate && Same(candidate->original, original) ? Transfer::AlreadyRetained : Transfer::Rejected;
  }
  if (!bootstrapped_ || poisoned_ || !gate_.AllowsAdmission() || !Valid(original)
      || original.completed || original.completedAtMs) return Transfer::Rejected;
  for (const auto& candidate : candidates_) if (candidate.occupied && candidate.original.operationId == original.operationId)
    return Transfer::Rejected;
  for (size_t index = 0; index < Capacity; ++index) {
    auto& candidate = candidates_[index];
    if (candidate.occupied || candidate.generation == std::numeric_limits<uint64_t>::max()) continue;
    ++candidate.generation; candidate.occupied = true; candidate.original = original; candidate.reserveIntent = true;
    handle.slot = static_cast<uint8_t>(index); handle.generation = candidate.generation;
    return Transfer::RetainedVolatile;
  }
  return Transfer::Busy;
}
OfflineEvidenceBridge::ReserveStatus OfflineEvidenceBridge::PollReserve(Handle handle) const {
  const auto candidate = Get(handle);
  return candidate ? candidate->reserve : ReserveStatus::Refused;
}
OfflineEvidenceBridge::Transfer OfflineEvidenceBridge::Complete(Handle handle, uint64_t completedAtMs) {
  auto candidate = Get(handle);
  if (!candidate || poisoned_ || candidate->conservative || candidate->terminal || !candidate->present
      || candidate->reserveIntent || completedAtMs < candidate->original.startedAtMs)
    return Transfer::Rejected;
  if (candidate->completionCaptured) return candidate->completedAtMs == completedAtMs
      ? Transfer::AlreadyRetained : Transfer::Rejected;
  candidate->completionCaptured = true; candidate->completedAtMs = completedAtMs; candidate->completeIntent = true;
  return Transfer::RetainedVolatile;
}
OfflineEvidenceBridge::Status OfflineEvidenceBridge::PollComplete(Handle handle) const {
  const auto candidate = Get(handle);
  return candidate ? candidate->complete : Status::Refused;
}
OfflineEvidenceBridge::Transfer OfflineEvidenceBridge::Ack(Handle handle, const Outcome& publishedCompleted) {
  auto candidate = Get(handle);
  if (!candidate || poisoned_ || candidate->conservative || candidate->terminal || !candidate->present
      || !publishedCompleted.completed || !Same(candidate->published, publishedCompleted)) return Transfer::Rejected;
  if (candidate->ackIntent) return Same(candidate->acknowledged, publishedCompleted)
      ? Transfer::AlreadyRetained : Transfer::Rejected;
  candidate->acknowledged = publishedCompleted; candidate->ackIntent = true;
  return Transfer::RetainedVolatile;
}
OfflineEvidenceBridge::Status OfflineEvidenceBridge::PollAck(Handle handle) const {
  const auto candidate = Get(handle);
  return candidate ? candidate->ack : Status::Refused;
}
bool OfflineEvidenceBridge::Cancel(Handle handle) {
  auto candidate = Get(handle);
  if (!candidate || poisoned_) return false;
  candidate->revoked = true;
  if (candidate->reserve == ReserveStatus::DurableReserved) candidate->reserve = ReserveStatus::Refused;
  if (candidate->reserveIntent && !candidate->present && !InFlight(handle)) {
    candidate->reserveIntent = false; candidate->terminal = true;
    candidate->reserve = ReserveStatus::CancelledBeforeWrite;
  }
  return true;
}
bool OfflineEvidenceBridge::Release(Handle handle) {
  auto candidate = Get(handle);
  if (!candidate || poisoned_ || !candidate->terminal || candidate->present || InFlight(handle)
      || candidate->reserveIntent || candidate->completeIntent || candidate->ackIntent) return false;
  const uint64_t generation = candidate->generation;
  *candidate = {}; candidate->generation = generation;
  return true;
}
OfflineEvidenceBridge::Transfer OfflineEvidenceBridge::TryQueueSettings(
    Lane lane, const EvidenceStorageOwner::Request& request, IntentHandle& handle) {
  if (handle.generation) {
    const auto intent = Get(handle);
    return intent && handle.lane == lane && SameRequest(intent->request, request) ? Transfer::AlreadyRetained : Transfer::Rejected;
  }
  if (!bootstrapped_ || poisoned_ || !ValidSettings(lane, request)) return Transfer::Rejected;
  if (nextSequence_ == std::numeric_limits<uint64_t>::max()) return Transfer::Rejected;
  auto& entries = settings_[static_cast<size_t>(lane)];
  for (size_t index = 0; index < entries.size(); ++index) {
    auto& intent = entries[index];
    if (intent.occupied || intent.generation == std::numeric_limits<uint64_t>::max()) continue;
    ++intent.generation; intent.sequence = ++nextSequence_; intent.occupied = intent.pending = true; intent.request = request;
    handle.lane = lane; handle.slot = static_cast<uint8_t>(index); handle.generation = intent.generation;
    if (lane == Lane::Stop) {
      for (size_t otherLane = 1; otherLane < settings_.size(); ++otherLane) {
        for (size_t otherSlot = 0; otherSlot < settings_[otherLane].size(); ++otherSlot) {
          auto& older = settings_[otherLane][otherSlot];
          IntentHandle olderHandle;
          olderHandle.lane = static_cast<Lane>(otherLane); olderHandle.slot = static_cast<uint8_t>(otherSlot);
          olderHandle.generation = older.generation;
          if (!older.occupied || !older.pending || older.sequence >= intent.sequence
              || InFlight(olderHandle) || !Relaxing(older.request)) continue;
          // The immutable request/result slot remains owned by its caller.
          // Recovery cannot revive an unsafe intent accepted before this Stop.
          older.pending = false; older.status = Status::Refused;
        }
      }
    }
    return Transfer::RetainedVolatile;
  }
  return Transfer::Busy;
}
OfflineEvidenceBridge::Status OfflineEvidenceBridge::PollSettings(IntentHandle handle) const {
  const auto intent = Get(handle);
  return intent ? intent->status : Status::Refused;
}
bool OfflineEvidenceBridge::ReleaseSettings(IntentHandle handle) {
  auto intent = Get(handle);
  if (!intent || poisoned_ || intent->pending || InFlight(handle) || intent->status == Status::Unknown) return false;
  const uint64_t generation = intent->generation;
  *intent = {}; intent->generation = generation;
  return true;
}
void OfflineEvidenceBridge::Adopt(const EvidenceStorageOwner::Completion& result) {
  if (poisoned_ || result.status == EvidenceStorageOwner::Status::Unknown) { Poison(); return; }
  const bool success = result.status == EvidenceStorageOwner::Status::Succeeded;
  if (flight_.isSettings) {
    auto intent = Get(flight_.settings);
    if (!intent) { Poison(); return; }
    if (success && !MatchesSettings(intent->request, publication_)) { Poison(); return; }
    intent->status = success && result.token.purpose == JournalTransactionArbiter::Purpose::Admission && !result.current
        ? Status::Refused : Convert(result.status);
    intent->pending = false;
    return;
  }
  auto candidate = Get(flight_.candidate);
  if (!candidate) { Poison(); return; }
  if (flight_.operation == Operation::OfflineReserve) {
    candidate->reserveIntent = false;
    if (candidate->present) {
      candidate->revoked |= !success || !result.current;
      candidate->reserve = candidate->revoked ? ReserveStatus::Refused : ReserveStatus::DurableReserved;
    } else {
      if (success) { Poison(); return; }
      candidate->reserve = result.status == EvidenceStorageOwner::Status::CancelledBeforeWrite
          ? ReserveStatus::CancelledBeforeWrite : ReserveStatus::Refused;
      candidate->terminal = true;
    }
  } else if (flight_.operation == Operation::OfflineComplete) {
    Outcome completed = candidate->original;
    completed.completed = true; completed.completedAtMs = candidate->completedAtMs;
    if (success && (!candidate->present || !Same(candidate->published, completed))) { Poison(); return; }
    candidate->complete = Convert(result.status);
    candidate->completeIntent = !success;
  } else if (flight_.operation == Operation::OfflineAck) {
    if (success && candidate->present) { Poison(); return; }
    candidate->ack = Convert(result.status); candidate->ackIntent = !success;
    if (success) candidate->terminal = true;
  }
}
bool OfflineEvidenceBridge::SubmitCandidate(size_t index) {
  auto& candidate = candidates_[index];
  if (!candidate.occupied || candidate.conservative || candidate.terminal) return false;
  EvidenceStorageOwner::Request request;
  request.offline = candidate.original;
  if (candidate.reserveIntent) request.operation = Operation::OfflineReserve;
  else if (candidate.ackIntent) { request.operation = Operation::OfflineAck; request.offline = candidate.acknowledged; }
  else if (candidate.completeIntent) {
    request.operation = Operation::OfflineComplete;
    request.offline.completed = true; request.offline.completedAtMs = candidate.completedAtMs;
  } else return false;
  EvidenceStorageOwner::Token token;
  if (!owner_.TrySubmit(request, token)) return false;
  flight_.token = token; flight_.candidate.slot = static_cast<uint8_t>(index);
  flight_.candidate.generation = candidate.generation; flight_.operation = request.operation;
  return true;
}
bool OfflineEvidenceBridge::SubmitSettings(Lane lane) {
  const auto& entries = settings_[static_cast<size_t>(lane)];
  size_t selected = entries.size();
  for (size_t index = 0; index < entries.size(); ++index) if (entries[index].occupied && entries[index].pending
      && (selected == entries.size() || entries[index].sequence < entries[selected].sequence)) selected = index;
  if (selected == entries.size()) return false;
  EvidenceStorageOwner::Token token;
  if (!owner_.TrySubmit(entries[selected].request, token)) return false;
  flight_.token = token; flight_.isSettings = true; flight_.settings.lane = lane;
  flight_.settings.slot = static_cast<uint8_t>(selected); flight_.settings.generation = entries[selected].generation;
  flight_.operation = entries[selected].request.operation;
  return true;
}
bool OfflineEvidenceBridge::Submit() {
  if (SubmitSettings(Lane::Stop)) return true;
  for (size_t count = 0; count < Capacity + 2; ++count) {
    const size_t producer = (nextProducer_ + count) % (Capacity + 2);
    const bool submitted = producer < Capacity ? SubmitCandidate(producer)
        : SubmitSettings(producer == Capacity ? Lane::Settings : Lane::Enabled);
    if (!submitted) continue;
    nextProducer_ = (producer + 1) % (Capacity + 2);
    return true;
  }
  return false;
}
bool OfflineEvidenceBridge::Pump() {
  if (flight_.token.id && flight_.adopted) {
    if (!owner_.ReleaseCompletion(flight_.token)) return false;
    flight_ = {};
  }
  EvidenceStorageOwner::Completion result;
  const bool completed = flight_.token.id && owner_.TryReadCompletion(flight_.token, result);
  Outbox state; uint64_t generation = 0;
  const bool published = owner_.TryReadOffline(state, &generation);
  if (!bootstrapped_) return false;
  if (!owner_.IsHealthy()) Poison();
  if (published) Refresh(state, generation);
  if (completed) {
    if (!poisoned_ && result.status != EvidenceStorageOwner::Status::Unknown
        && (!published || generation < result.offlineGeneration)) return false;
    Adopt(result); flight_.adopted = true;
    return true;
  }
  if (poisoned_ || flight_.token.id) return false;
  return Submit();
}
bool OfflineEvidenceBridge::HasWork() const {
  if (poisoned_ || flight_.token.id) return true;
  for (const auto& candidate : candidates_) if (candidate.occupied
      && (candidate.reserveIntent || candidate.completeIntent || candidate.ackIntent)) return true;
  for (const auto& lane : settings_) for (const auto& intent : lane) if (intent.occupied) return true;
  return false;
}
size_t OfflineEvidenceBridge::Occupied() const {
  size_t occupied = 0;
  for (const auto& candidate : candidates_) occupied += candidate.occupied;
  return occupied;
}

} }

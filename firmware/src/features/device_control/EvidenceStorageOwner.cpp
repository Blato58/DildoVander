#include "EvidenceStorageOwner.h"

#include <algorithm>
#include <climits>
#include <cstring>

#include "PersistentCommandJournal.h"
#include "JournalAdmissionGate.h"
#include "../keepsake/KeepsakeCommittedInventory.h"
#include "../keepsake/KeepsakePersistenceCodec.h"
#include "../offline_show/OfflineCommittedInventory.h"

namespace {
template <size_t Size>
bool Uuid(const std::array<char, Size>& value) {
  static_assert(Size == 37, "UUID storage includes one terminator");
  if (value[36]) return false;
  for (size_t index = 0; index < 36; ++index) {
    const char character = JournalIdentityLower(value[index]);
    if (index == 8 || index == 13 || index == 18 || index == 23) {
      if (character != '-') return false;
    } else if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))) return false;
  }
  return true;
}
bool CopyUuid(const std::string& source, std::array<char, 37>& target) {
  if (source.size() != 36) return false;
  std::copy(source.begin(), source.end(), target.begin());
  target.back() = 0;
  return Uuid(target);
}
bool RetirementReason(const char* reason) {
  static const char* const reasons[] = {"proposal_store_rejected", "proposal_stale_or_expired", "proposal_stopped",
      "proposal_cancelled", "presentation_fault", "proposal_start_rejected", "conversation_closed",
      "completion_unconfirmed", "physical_outcome_unknown", "outcome_deleted", "outcome_not_eligible",
      "outcome_snapshot_conflict", "outcome_conflict", "operator_restore_required", "idempotency_conflict", "keepsake_not_found"};
  for (const char* candidate : reasons) if (std::strcmp(reason, candidate) == 0) return true;
  return false;
}
KeepsakeOutcome Expand(const EvidenceStorageOwner::OnlineOutcome& value) {
  KeepsakeOutcome outcome;
  outcome.operationId = value.operationId.data(); outcome.outcomeId = value.outcomeId.data();
  outcome.proposalId = value.proposalId.data(); outcome.physicalOperationId = value.physicalOperationId.data();
  outcome.bootId = value.bootId.data(); outcome.generation = value.generation;
  outcome.deviceRevision = value.deviceRevision; outcome.retryAtMs = value.retryAtMs;
  outcome.retryDelayMs = value.retryDelayMs; outcome.compartment = value.compartment;
  outcome.operationEpoch = value.operationEpoch; outcome.physicalFingerprint = value.physicalFingerprint;
  return outcome;
}
bool Contains(const std::vector<KeepsakeOutcome>& records, const KeepsakeOutcome& candidate) {
  for (const auto& record : records) if (SameKeepsakeCandidate(record, candidate)) return true;
  return false;
}
bool SameOffline(const kufr::offline::Outcome& left, const kufr::offline::Outcome& right) {
  return left.operationId == right.operationId && left.bootId == right.bootId
      && left.localEveningId == right.localEveningId && left.generation == right.generation
      && left.startedAtMs == right.startedAtMs && left.slot == right.slot
      && left.scripted == right.scripted && left.shortVariant == right.shortVariant
      && left.operationEpoch == right.operationEpoch && left.physicalFingerprint == right.physicalFingerprint
      && left.deviceRevision == right.deviceRevision;
}
bool EmptyOffline(const kufr::offline::Outcome& record) {
  for (char value : record.operationId) if (value) return false;
  for (char value : record.bootId) if (value) return false;
  for (char value : record.localEveningId) if (value) return false;
  return !record.generation && !record.startedAtMs && !record.completedAtMs && !record.slot
      && !record.completed && !record.scripted && !record.shortVariant && !record.operationEpoch
      && !record.physicalFingerprint && !record.deviceRevision;
}
}

EvidenceStorageOwner::EvidenceStorageOwner(PersistentCommandJournal& journal,
    KeepsakeCommittedInventory& online, kufr::offline::OfflineCommittedInventory& offline,
    JournalTransactionArbiter& arbiter, JournalAdmissionGate* gate)
    : journal_(journal), online_(online), offline_(offline), arbiter_(arbiter), gate_(gate) {}

bool EvidenceStorageOwner::Same(const Token& left, const Token& right) {
  return left.id && left.id == right.id && left.cancellation == right.cancellation && left.purpose == right.purpose;
}
bool EvidenceStorageOwner::OnlineOperation(Operation operation) {
  return operation == Operation::OnlinePrepare || operation == Operation::OnlineComplete
      || operation == Operation::OnlineRetire || operation == Operation::OnlineAck;
}

bool EvidenceStorageOwner::MakeOnlineRequest(Operation operation, const KeepsakeOutcome& outcome,
    Request& request, const char* reason) {
  if (!OnlineOperation(operation) || !reason) return false;
  size_t length = 0;
  while (length <= 64 && reason[length]) ++length;
  if (length > 64) return false;
  Request next;
  next.operation = operation;
  auto& value = next.online;
  if (!CopyUuid(outcome.operationId, value.operationId) || !CopyUuid(outcome.outcomeId, value.outcomeId)
      || !CopyUuid(outcome.proposalId, value.proposalId) || !CopyUuid(outcome.physicalOperationId, value.physicalOperationId)
      || !CopyUuid(outcome.bootId, value.bootId)) return false;
  value.generation = outcome.generation; value.deviceRevision = outcome.deviceRevision;
  value.retryAtMs = outcome.retryAtMs; value.retryDelayMs = outcome.retryDelayMs;
  value.compartment = outcome.compartment; value.operationEpoch = outcome.operationEpoch;
  value.physicalFingerprint = outcome.physicalFingerprint;
  std::copy(reason, reason + length, next.reason.begin());
  if (!ValidRequest(next)) return false;
  request = next;
  return true;
}
bool EvidenceStorageOwner::MakeOfflineRequest(Operation operation,
    const kufr::offline::Outcome& outcome, Request& request) {
  if (operation != Operation::OfflineReserve && operation != Operation::OfflineComplete && operation != Operation::OfflineAck)
    return false;
  Request next; next.operation = operation; next.offline = outcome;
  if (!ValidRequest(next)) return false;
  request = next;
  return true;
}

bool EvidenceStorageOwner::ValidRequest(const Request& request) {
  if (OnlineOperation(request.operation)) {
    const auto& value = request.online;
    if (!Uuid(value.operationId) || !Uuid(value.outcomeId) || !Uuid(value.proposalId)
        || !Uuid(value.physicalOperationId) || !Uuid(value.bootId) || value.compartment < 1 || value.compartment > 6
        || value.generation > INT32_MAX || value.deviceRevision > INT32_MAX
        || (value.operationEpoch && !value.physicalFingerprint) || request.reason.back()) return false;
    if (request.operation == Operation::OnlinePrepare && (!value.operationEpoch || !value.physicalFingerprint)) return false;
    if (request.operation == Operation::OnlineRetire) return RetirementReason(request.reason.data());
    return request.reason.front() == 0;
  }
  switch (request.operation) {
    case Operation::OfflineReserve:
    case Operation::OfflineComplete:
    case Operation::OfflineAck: {
      const auto& value = request.offline;
      if (!Uuid(value.operationId) || !Uuid(value.bootId) || !Uuid(value.localEveningId)
          || value.slot < 1 || value.slot > 6 || !value.operationEpoch || !value.physicalFingerprint || !value.generation)
        return false;
      if (request.operation == Operation::OfflineReserve) return !value.completed && !value.completedAtMs;
      return value.completed && value.completedAtMs >= value.startedAtMs;
    }
    case Operation::OfflineSettings:
      return (request.setQuiet || request.setAmbient || request.serverStopRevision)
          && !request.setEnabled && (!request.serverSettingsRevision || (request.setQuiet && request.setAmbient));
    case Operation::OfflineEnabled:
      return request.setEnabled && !request.setQuiet && !request.setAmbient
          && !request.serverSettingsRevision && !request.serverStopRevision;
    default: return false;
  }
}

bool EvidenceStorageOwner::TrySubmit(const Request& request, Token& token) {
  token = {};
  if (!ValidRequest(request)) return false;
  std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
  if (!lock || phase_ != Phase::Idle) return false;
  const auto purpose = request.operation == Operation::OnlinePrepare || request.operation == Operation::OfflineReserve
      ? JournalTransactionArbiter::Purpose::Admission : JournalTransactionArbiter::Purpose::Evidence;
  if (!GateAllows(purpose)) return false;
  Token accepted;
  if (!arbiter_.TryBegin(purpose, accepted)) return false;
  request_ = request; result_ = {}; result_.token = accepted;
  token = accepted; phase_ = Phase::Queued;
  return true;
}

void EvidenceStorageOwner::UnpublishOnline() {
  std::shared_ptr<const KeepsakeOutboxState> previous;
  { std::lock_guard<std::mutex> lock(mutex_); previous.swap(onlinePublished_); onlineGeneration_ = 0; onlineAvailable_.store(false); }
}
void EvidenceStorageOwner::UnpublishOffline() {
  std::lock_guard<std::mutex> lock(mutex_); offlineAvailable_ = false; offlineGeneration_ = 0;
}
bool EvidenceStorageOwner::ReadOnline(KeepsakeOutboxState& state, uint64_t& generation) {
  JournalEvidenceInventory inventory;
  if (!online_.IsBound() || !online_.ReadCommitted(inventory) || !inventory.complete
      || !journal_.IsHealthy() || inventory.deviceId != journal_.ScopedDeviceId() || !online_.ReadState(state)) {
    UnpublishOnline(); return false;
  }
  generation = inventory.generation;
  return true;
}
bool EvidenceStorageOwner::ReadOffline(kufr::offline::Outbox& state) {
  if (!offline_.IsBound() || !journal_.IsHealthy() || offline_.BoundDeviceId() != journal_.ScopedDeviceId()
      || !offline_.Read(state)) { UnpublishOffline(); return false; }
  return true;
}
bool EvidenceStorageOwner::OpenAndPublish() {
  { std::lock_guard<std::mutex> lock(mutex_); if (phase_ != Phase::Idle) return false; }
  online_.Reopen(); offline_.Reopen();
  KeepsakeOutboxState onlineState;
  kufr::offline::Outbox offlineState;
  uint64_t generation = 0;
  const bool onlineReady = ReadOnline(onlineState, generation);
  const bool offlineReady = ReadOffline(offlineState);
  auto publication = onlineReady ? std::make_shared<const KeepsakeOutboxState>(std::move(onlineState)) : nullptr;
  {
    std::lock_guard<std::mutex> lock(mutex_);
    onlinePublished_.swap(publication); onlineGeneration_ = onlineReady ? generation : 0;
    onlineAvailable_.store(onlineReady);
    offlinePublished_ = offlineState; offlineAvailable_ = offlineReady;
    offlineGeneration_ = offlineReady ? offline_.Generation() : 0;
  }
  return onlineReady && offlineReady;
}

bool EvidenceStorageOwner::RecoverProvenCompletions() {
  if (!IsHealthy() || journal_.HasPresentationReservation() || !arbiter_.TryEnter()) return false;
  struct Release final {
    JournalTransactionArbiter& arbiter;
    ~Release() { arbiter.Leave(); }
  } release{arbiter_};
  KeepsakeOutboxState state;
  uint64_t generation = 0;
  if (!ReadOnline(state, generation)) return false;
  KeepsakeOutbox outbox;
  outbox.RetainRetirementLedger();
  if (!outbox.Restore(state.pending, state.quarantined, state.prepared)) return false;
  for (const auto& outcome : state.prepared) {
    if (!HasKeepsakePhysicalEvidence(journal_, outcome)) continue;
    if (outbox.Queue(outcome) == KeepsakeQueueResult::Rejected) return false;
    outbox.RemovePrepared(outcome.outcomeId, outcome.operationEpoch);
  }
  // No absence of proof, lost volatile capability or inspected availability
  // supplies a new outcome. Unknown entries remain pinned and undelivered.
  return CommitOnline(outbox.State());
}

bool EvidenceStorageOwner::CanCreateReference(uint64_t epoch, const std::string& id,
    uint8_t slot, uint64_t fingerprint, const Token& token) const {
  if (!epoch || epoch != journal_.CurrentEpoch() || !fingerprint || arbiter_.AdmissionsStopped()
      || !arbiter_.IsCurrent(token) || !journal_.IsEvidencePinned(epoch, id, slot, fingerprint)) return false;
  if (journal_.HasPresentationReservation(epoch, id, slot, fingerprint)) return true;
  JournalSnapshot snapshot;
  if (!journal_.ExportSnapshot(snapshot) || !snapshot.healthy || snapshot.currentEpoch != epoch
      || snapshot.pendingSlot != slot) return false;
  const JournalOperationIdentity pending{epoch, snapshot.pendingId, slot, fingerprint};
  return SameJournalOperationIdentity(pending, {epoch, id, slot, fingerprint})
      && journal_.FindHistorical(epoch, id, slot, fingerprint) == CommandJournal::Lookup::Same;
}

bool EvidenceStorageOwner::GateAllows(JournalTransactionArbiter::Purpose purpose) const {
  return !gate_ || (purpose == JournalTransactionArbiter::Purpose::Admission
      ? gate_->AllowsAdmission() : gate_->AllowsRecovery());
}
bool EvidenceStorageOwner::CommitOnline(const KeepsakeOutboxState& state, const Token* admission) {
  // Prepare every publication allocation before the source write. A failed ACK
  // hides the cache until authoritative reopen, even if bytes reached flash.
  KeepsakeOutboxState previous;
  uint64_t generation = 0;
  if (!ReadOnline(previous, generation)) return false;
  const auto payload = keepsake_persistence::Encode(state);
  const bool changed = keepsake_persistence::Encode(previous) != payload;
  KeepsakeOutboxState canonical;
  if (!keepsake_persistence::Decode(payload.c_str(), payload.size() + 1, canonical)) return false;
  std::shared_ptr<const KeepsakeOutboxState> publication = std::make_shared<const KeepsakeOutboxState>(std::move(canonical));
  if (admission && (arbiter_.AdmissionsStopped() || !arbiter_.IsCurrent(*admission) || !GateAllows(admission->purpose)))
    return false;
  if (!online_.Commit(state)) { if (!online_.IsHealthy()) UnpublishOnline(); return false; }
  {
    std::lock_guard<std::mutex> lock(mutex_);
    onlinePublished_.swap(publication); onlineGeneration_ = generation + changed; onlineAvailable_.store(true);
  }
  return true;
}
bool EvidenceStorageOwner::CommitOffline(const kufr::offline::Outbox& state, const Token* admission) {
  if (admission && (arbiter_.AdmissionsStopped() || !arbiter_.IsCurrent(*admission) || !GateAllows(admission->purpose)))
    return false;
  if (!offline_.Write(state)) { UnpublishOffline(); return false; }
  std::lock_guard<std::mutex> lock(mutex_);
  offlinePublished_ = state; offlineGeneration_ = offline_.Generation(); offlineAvailable_ = true;
  return true;
}

bool EvidenceStorageOwner::ExecuteOnline(const Request& request, const Token& token) {
  KeepsakeOutboxState state;
  uint64_t generation = 0;
  if (!ReadOnline(state, generation)) return false;
  const auto outcome = Expand(request.online);
  const bool prepared = Contains(state.prepared, outcome), pending = Contains(state.pending, outcome);
  KeepsakeOutbox outbox;
  outbox.RetainRetirementLedger();
  if (!outbox.Restore(state.pending, state.quarantined, state.prepared)) return false;
  const auto persist = [&](const KeepsakeOutboxState& next) { return CommitOnline(next); };
  switch (request.operation) {
    case Operation::OnlinePrepare:
      return CanCreateReference(outcome.operationEpoch, outcome.physicalOperationId, outcome.compartment,
          outcome.physicalFingerprint, token) && outbox.Prepare(outcome) && CommitOnline(outbox.State(), &token);
    case Operation::OnlineComplete:
      if ((!prepared && !pending) || !HasKeepsakePhysicalEvidence(journal_, outcome)
          || outbox.Queue(outcome) == KeepsakeQueueResult::Rejected) return false;
      outbox.RemovePrepared(outcome.outcomeId, outcome.operationEpoch);
      return CommitOnline(outbox.State());
    case Operation::OnlineAck:
      // An unassociated legacy row cannot acquire a terminal physical tuple
      // from delivery metadata. Keep it intact for explicit reconciliation.
      if (!outcome.physicalFingerprint) return false;
      if (!pending) {
        for (const auto& disposition : state.quarantined)
          if (disposition.reason == "outcome_delivered" && ExactKeepsakeDisposition(disposition, outcome)) return true;
        return false;
      }
      return outbox.RequestDeliveryAcknowledgement(outcome) && outbox.ProcessDeliveryAcknowledgements(persist);
    case Operation::OnlineRetire:
      if (!outcome.physicalFingerprint) return false;
      if (!pending) {
        if (prepared) return outbox.RequestRetirement(outcome, request.reason.data())
            && outbox.ProcessRetirements(journal_, persist);
        for (const auto& disposition : state.quarantined)
          if (disposition.reason == request.reason.data() && ExactKeepsakeDisposition(disposition, outcome)) return true;
        return false;
      }
      // A permanent delivery failure retires an existing completed obligation.
      // Preparation cancellation cannot erase a known completed pending row.
      if (!PermanentKeepsakeFailure(410, request.reason.data()) && !PermanentKeepsakeFailure(409, request.reason.data())
          && !PermanentKeepsakeFailure(404, request.reason.data())) return false;
      if (!outbox.RecordDisposition(outcome, request.reason.data())) return false;
      outbox.Complete(outcome.outcomeId, outcome.operationEpoch);
      outbox.RemovePrepared(outcome.outcomeId, outcome.operationEpoch);
      // The exact terminal tuple replaces both live references in one atomic
      // source transaction. A lost ACK exposes either all old or all new facts.
      return CommitOnline(outbox.State());
    default: return false;
  }
}

bool EvidenceStorageOwner::ExecuteOffline(const Request& request, const Token& token) {
  kufr::offline::Outbox state;
  if (!ReadOffline(state)) return false;
  const auto& outcome = request.offline;
  if (request.operation == Operation::OfflineSettings) {
    if (request.serverSettingsRevision && request.serverSettingsRevision <= state.serverSettingsRevision) return false;
    if (request.serverStopRevision && request.serverStopRevision <= state.serverStopRevision) return false;
    if (request.setQuiet) state.quiet = request.quiet;
    if (request.setAmbient) state.ambientEnabled = request.ambient;
    if (request.serverSettingsRevision) state.serverSettingsRevision = request.serverSettingsRevision;
    if (request.serverStopRevision) {
      state.serverStopRevision = request.serverStopRevision;
      state.quiet = true; state.ambientEnabled = false; state.offlineEnabled = false;
    }
    return CommitOffline(state);
  }
  if (request.operation == Operation::OfflineEnabled) {
    state.offlineEnabled = request.enabled;
    return CommitOffline(state);
  }
  size_t exact = state.records.size(), empty = state.records.size();
  for (size_t index = 0; index < state.records.size(); ++index) {
    const auto& saved = state.records[index];
    if (EmptyOffline(saved)) { if (empty == state.records.size()) empty = index; continue; }
    if (saved.operationEpoch != outcome.operationEpoch || saved.operationId != outcome.operationId) continue;
    if (!SameOffline(saved, outcome) || exact != state.records.size()) return false;
    exact = index;
  }
  if (request.operation == Operation::OfflineReserve) {
    if (!CanCreateReference(outcome.operationEpoch, outcome.operationId.data(), outcome.slot,
        outcome.physicalFingerprint, token)) return false;
    if (exact != state.records.size()) return !state.records[exact].completed;
    if (empty == state.records.size()) return false;
    state.records[empty] = outcome;
    return CommitOffline(state, &token);
  }
  if (exact == state.records.size()) return false;
  auto& saved = state.records[exact];
  if (request.operation == Operation::OfflineComplete) {
    if (!journal_.WasTimedPresentationCompletedHistorical(outcome.operationEpoch, outcome.operationId.data(),
        outcome.slot, outcome.physicalFingerprint)) return false;
    if (saved.completed) return saved.completedAtMs == outcome.completedAtMs;
    saved = outcome;
    return CommitOffline(state);
  }
  if (request.operation == Operation::OfflineAck && saved.completed && saved.completedAtMs == outcome.completedAtMs) {
    saved = {};
    return CommitOffline(state);
  }
  return false;
}

bool EvidenceStorageOwner::Execute(const Request& request, const Token& token) {
  return OnlineOperation(request.operation) ? ExecuteOnline(request, token) : ExecuteOffline(request, token);
}
bool EvidenceStorageOwner::RunOne() {
  Request request;
  Token token;
  {
    std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
    if (!lock || phase_ != Phase::Queued) return false;
    request = request_; token = result_.token; phase_ = Phase::Running;
  }
  Status status = token.purpose == JournalTransactionArbiter::Purpose::Admission && !arbiter_.IsCurrent(token)
      ? Status::CancelledBeforeWrite : Status::Refused;
  if (GateAllows(token.purpose) &&
      (token.purpose != JournalTransactionArbiter::Purpose::Admission || arbiter_.IsCurrent(token))) {
    const bool success = Execute(request, token);
    const bool healthy = OnlineOperation(request.operation) ? online_.IsHealthy() && online_.IsBound()
        : offline_.IsHealthy() && offline_.IsBound();
    status = success ? Status::Succeeded : !healthy ? Status::Unknown :
        token.purpose == JournalTransactionArbiter::Purpose::Admission && !arbiter_.IsCurrent(token)
            ? Status::CancelledBeforeWrite : Status::Refused;
  }
  std::lock_guard<std::mutex> lock(mutex_);
  result_.status = status; result_.onlineGeneration = onlineGeneration_; result_.offlineGeneration = offlineGeneration_;
  phase_ = Phase::Completed;
  return true;
}
bool EvidenceStorageOwner::TryReadCompletion(const Token& token, Completion& completion) const {
  std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
  if (!lock || phase_ != Phase::Completed || !Same(token, result_.token)) return false;
  completion = result_; completion.current = arbiter_.IsCurrent(token);
  return true;
}
bool EvidenceStorageOwner::ReleaseCompletion(const Token& token) {
  std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
  if (!lock || phase_ != Phase::Completed || !Same(token, result_.token) || !arbiter_.Release(token)) return false;
  phase_ = Phase::Idle;
  return true;
}
bool EvidenceStorageOwner::TryReadOnline(KeepsakeOutboxState& state, uint64_t* generation) const {
  std::shared_ptr<const KeepsakeOutboxState> publication;
  uint64_t publishedGeneration = 0;
  {
    std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
    if (!lock || !onlinePublished_) return false;
    publication = onlinePublished_; publishedGeneration = onlineGeneration_;
  }
  state = *publication;
  if (generation) *generation = publishedGeneration;
  return true;
}
bool EvidenceStorageOwner::TryReadOffline(kufr::offline::Outbox& state, uint64_t* generation) const {
  std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
  if (!lock || !offlineAvailable_) return false;
  state = offlinePublished_;
  if (generation) *generation = offlineGeneration_;
  return true;
}
bool EvidenceStorageOwner::Busy() const {
  std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
  return !lock || phase_ != Phase::Idle;
}

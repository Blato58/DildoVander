#include "JournalStorageOwner.h"

#include "PersistentCommandJournal.h"

JournalStorageOwner::JournalStorageOwner(
    PersistentCommandJournal& journal, JournalTransactionArbiter& arbiter, JournalAdmissionGate* admission)
    : journal_(journal), arbiter_(arbiter), admission_(admission) {
  journal_.ExportSnapshot(published_); // Before either owner task starts.
  observedCancellation_ = arbiter_.Cancellation();
  publishedCancellation_ = observedCancellation_;
}

bool JournalStorageOwner::Same(const Token& left, const Token& right) {
  return left.id && left.id == right.id && left.cancellation == right.cancellation && left.purpose == right.purpose;
}

bool JournalStorageOwner::ValidRequest(const Request& request, Purpose purpose) {
  if ((request.readyMask & 0xc0) != 0 || request.id.back() != 0) return false;
  if (request.technicalTest && request.operation != Operation::Reserve) return false;
  switch (request.operation) {
    case Operation::Begin:
    case Operation::Reserve:
    case Operation::BeginReserved:
      if (purpose != Purpose::Admission || request.slot < 1 || request.slot > 6) return false;
      break;
    case Operation::Resolve:
      return purpose == Purpose::Recovery || purpose == Purpose::Evidence;
    case Operation::ResolveRestored:
      return purpose == Purpose::Evidence && request.slot >= 1 && request.slot <= 6;
    case Operation::Reload:
      return purpose == Purpose::Admission || purpose == Purpose::Recovery;
    case Operation::ConfirmTimed:
      if (purpose != Purpose::Evidence || request.slot < 1 || request.slot > 6 || !request.fingerprint) return false;
      break;
    default: return false;
  }
  for (size_t index = 0; index < 36; ++index) {
    const char value = request.id[index];
    if (index == 8 || index == 13 || index == 18 || index == 23) {
      if (value != '-') return false;
    } else if (!((value >= '0' && value <= '9') || (value >= 'a' && value <= 'f') ||
                 (value >= 'A' && value <= 'F'))) return false;
  }
  return true;
}

bool JournalStorageOwner::TrySubmit(const Request& request, Purpose purpose, Token& token) {
  return TrySubmitTyped(request, purpose, token) == Submission::Accepted;
}
JournalStorageOwner::Submission JournalStorageOwner::TrySubmitTyped(const Request& request, Purpose purpose, Token& token) {
  token = {};
  if (!ValidRequest(request, purpose)) return Submission::Rejected;
  if (admission_ && !(purpose == Purpose::Admission
      ? admission_->AllowsAdmission() : admission_->AllowsRecovery()))
    return purpose == Purpose::Recovery ? Submission::Unknown : Submission::Rejected;
  std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
  if (!lock || phase_ != Phase::Idle) return Submission::Busy;
  if (!published_.healthy) return Submission::Unknown;
  Token accepted;
  const auto reservation = arbiter_.TryBeginDetailed(purpose, accepted);
  if (reservation == JournalTransactionArbiter::Result::Busy) return Submission::Busy;
  if (reservation != JournalTransactionArbiter::Result::Accepted) return Submission::Rejected;
  // Both request and completion storage already exist. Publication can never
  // need queue space or a heap allocation after a flash commit.
  request_ = request;
  result_ = {};
  result_.token = accepted;
  token = accepted;
  phase_ = Phase::Queued;
  return Submission::Accepted;
}

bool JournalStorageOwner::Execute(const Request& request) {
  const std::string id(request.id.data()); // Bounded allocation before journal I/O.
  switch (request.operation) {
    case Operation::Begin:
      return request.epoch ? journal_.BeginScoped(request.epoch, id, request.slot, request.fingerprint)
          : journal_.BeginWithIdentity(id, request.slot, request.fingerprint);
    case Operation::Reserve:
      return request.technicalTest
          ? journal_.ReserveTechnicalPresentation(request.epoch, id, request.slot, request.fingerprint)
          : journal_.ReservePresentation(request.epoch, id, request.slot, request.fingerprint);
    case Operation::BeginReserved:
      return journal_.BeginReservedPresentation(request.epoch, id, request.slot, request.fingerprint);
    case Operation::Resolve: return journal_.Resolve(request.readyMask);
    case Operation::ResolveRestored: return journal_.ResolveRestored(request.slot);
    case Operation::ConfirmTimed:
      return request.epoch ? journal_.ConfirmTimedPresentationScoped(request.epoch, id, request.slot, request.fingerprint)
          : journal_.ConfirmTimedPresentation(id, request.slot, request.fingerprint);
    case Operation::Reload: return journal_.PersistReadyMask(request.readyMask);
  }
  return false;
}

bool JournalStorageOwner::RunOne() {
  const bool cancelled = ObserveCancellation();
  Request request;
  Token token;
  {
    std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
    if (!lock || phase_ != Phase::Queued) return cancelled;
    request = request_;
    token = result_.token;
    phase_ = Phase::Running;
  }
  // There is no arbiter or mailbox mutex held across this potentially slow I/O.
  // Evidence/recovery jobs retain their original durable obligations after Stop.
  Status status = Status::CancelledBeforeWrite;
  const bool authority = !admission_ || (token.purpose == Purpose::Admission
      ? admission_->AllowsAdmission() : admission_->AllowsRecovery());
  if (authority && (token.purpose != Purpose::Admission || arbiter_.IsCurrent(token))) {
    const bool success = Execute(request);
    status = success ? Status::Succeeded : journal_.IsHealthy() ? Status::Refused : Status::Unknown;
  }
  ObserveCancellation();
  JournalSnapshot snapshot;
  journal_.ExportSnapshot(snapshot);
  {
    std::lock_guard<std::mutex> lock(mutex_);
    // RunOne has one worker owner; the result cannot be released while Running.
    result_.status = status;
    result_.snapshot = snapshot;
    result_.publicationCancellation = observedCancellation_;
    published_ = snapshot;
    publishedCancellation_ = observedCancellation_;
    phase_ = Phase::Completed;
  }
  return true;
}

bool JournalStorageOwner::ObserveCancellation() {
  const uint32_t cancellation = arbiter_.Cancellation();
  if (cancellation == observedCancellation_) return false;
  journal_.CancelPresentationReservation(); // Volatile authority only; no NVS.
  observedCancellation_ = cancellation;
  JournalSnapshot snapshot;
  journal_.ExportSnapshot(snapshot);
  std::lock_guard<std::mutex> lock(mutex_);
  published_ = snapshot;
  publishedCancellation_ = observedCancellation_;
  if (phase_ == Phase::Completed) {
    result_.snapshot = snapshot;
    result_.publicationCancellation = observedCancellation_;
  }
  return true;
}

bool JournalStorageOwner::TryReadCompletion(const Token& token, Completion& completion) const {
  std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
  if (!lock || phase_ != Phase::Completed || !Same(result_.token, token)) return false;
  completion = result_;
  completion.current = arbiter_.IsCurrent(token);
  return true;
}

bool JournalStorageOwner::ReleaseCompletion(const Token& token) {
  std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
  if (!lock || phase_ != Phase::Completed || !Same(result_.token, token)) return false;
  if (!arbiter_.Release(token)) return false;
  phase_ = Phase::Idle;
  return true;
}

bool JournalStorageOwner::TryReadPublished(JournalSnapshot& snapshot, uint32_t* cancellation) const {
  std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
  if (!lock) return false;
  snapshot = published_;
  if (cancellation) *cancellation = publishedCancellation_;
  return true;
}

bool JournalStorageOwner::Busy() const {
  std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
  return !lock || phase_ != Phase::Idle;
}

bool JournalStorageOwner::RefreshPublication() {
  ObserveCancellation();
  JournalSnapshot snapshot;
  journal_.ExportSnapshot(snapshot);
  std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
  if (!lock || phase_ == Phase::Running || phase_ == Phase::Completed) return false;
  published_ = snapshot;
  publishedCancellation_ = observedCancellation_;
  return true;
}

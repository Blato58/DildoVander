#include "AsyncCommandJournal.h"

#include <algorithm>

AsyncCommandJournal::AsyncCommandJournal(JournalStorageOwner& owner, JournalTransactionArbiter& arbiter)
    : owner_(owner), arbiter_(arbiter), recoveryCancellation_(arbiter.Cancellation()) {
  owner_.TryReadPublished(snapshot_, &snapshotCancellation_);
}

bool AsyncCommandJournal::Same(const Request& left, const Request& right) {
  return left.operation == right.operation && left.epoch == right.epoch && left.id == right.id &&
      left.slot == right.slot && left.readyMask == right.readyMask && left.fingerprint == right.fingerprint;
}

void AsyncCommandJournal::Adopt(const JournalStorageOwner::Completion& completion) {
  // Cancellation can invalidate actuation, never the committed consequences.
  snapshot_ = completion.snapshot;
  snapshotCancellation_ = completion.publicationCancellation;
}

void AsyncCommandJournal::PollStorage() {
  if (!token_.id) { owner_.TryReadPublished(snapshot_, &snapshotCancellation_); return; }
  JournalStorageOwner::Completion completion;
  if (!owner_.TryReadCompletion(token_, completion)) return;
  Adopt(completion);
  if (!abandoned_) return; // Original caller must consume its own final result.
  if (!owner_.ReleaseCompletion(token_)) return;
  token_ = {};
  abandoned_ = false;
  lastState_ = StorageState::Superseded;
}

bool AsyncCommandJournal::Apply(const Request& request, Purpose purpose) {
  PollStorage();
  if (token_.id) {
    if (abandoned_ || !Same(request, request_) || purpose != token_.purpose) {
      lastState_ = StorageState::Rejected;
      return false;
    }
    JournalStorageOwner::Completion completion;
    if (!owner_.TryReadCompletion(token_, completion)) {
      lastState_ = StorageState::Pending;
      return false;
    }
    Adopt(completion);
    if (!owner_.ReleaseCompletion(token_)) { lastState_ = StorageState::Pending; return false; }
    token_ = {};
    if (completion.status == JournalStorageOwner::Status::Unknown) lastState_ = StorageState::Unknown;
    else if (!completion.current || completion.status == JournalStorageOwner::Status::CancelledBeforeWrite)
      lastState_ = StorageState::Superseded;
    else if (completion.status == JournalStorageOwner::Status::Succeeded) lastState_ = StorageState::Committed;
    else lastState_ = StorageState::Rejected;
    if (purpose == Purpose::Recovery && lastState_ == StorageState::Committed)
      recoveryCancellation_ = completion.token.cancellation;
    return lastState_ == StorageState::Committed;
  }
  if (!owner_.TrySubmit(request, purpose, token_)) { lastState_ = StorageState::Rejected; return false; }
  request_ = request;
  lastState_ = StorageState::Pending;
  return false;
}

bool AsyncCommandJournal::Identified(JournalStorageOwner::Operation operation, Purpose purpose,
    uint64_t epoch, const std::string& id, uint8_t slot, uint64_t fingerprint) {
  if (id.size() != 36) { lastState_ = StorageState::Rejected; return false; }
  Request request;
  request.operation = operation; request.epoch = epoch; request.slot = slot; request.fingerprint = fingerprint;
  std::copy(id.begin(), id.end(), request.id.begin());
  return Apply(request, purpose);
}
bool AsyncCommandJournal::BeginWithIdentity(const std::string& id, uint8_t slot, uint64_t fingerprint) {
  return Identified(JournalStorageOwner::Operation::Begin, Purpose::Admission, 0, id, slot, fingerprint);
}
bool AsyncCommandJournal::BeginScoped(uint64_t epoch, const std::string& id, uint8_t slot, uint64_t fingerprint) {
  return Identified(JournalStorageOwner::Operation::Begin, Purpose::Admission, epoch, id, slot, fingerprint);
}
bool AsyncCommandJournal::ReservePresentation(uint64_t epoch, const std::string& id, uint8_t slot, uint64_t fingerprint) {
  return Identified(JournalStorageOwner::Operation::Reserve, Purpose::Admission, epoch, id, slot, fingerprint);
}
bool AsyncCommandJournal::BeginReservedPresentation(uint64_t epoch, const std::string& id, uint8_t slot, uint64_t fingerprint) {
  return Identified(JournalStorageOwner::Operation::BeginReserved, Purpose::Admission, epoch, id, slot, fingerprint);
}
void AsyncCommandJournal::CancelPresentationReservation() {
  const bool reserving = token_.id &&
      (request_.operation == JournalStorageOwner::Operation::Reserve ||
       request_.operation == JournalStorageOwner::Operation::BeginReserved);
  if (!reserving && !HasPresentationReservation()) return;
  arbiter_.InvalidateAdmissions();
  // A reservation cancellation must not discard an unrelated closing receipt.
  abandoned_ = reserving;
  snapshot_.reservation = {};
  PollStorage();
}
void AsyncCommandJournal::CancelStorageAdmissions() {
  owner_.CancelAdmissions();
  abandoned_ = token_.id != 0;
  snapshot_.reservation = {};
  PollStorage();
}
bool AsyncCommandJournal::ResumeStorageAfterRecovery() {
  PollStorage();
  if (token_.id) return false;
  return arbiter_.ResumeAfterRecovery(recoveryCancellation_);
}
void AsyncCommandJournal::BeginStorageRecovery() {
  PollStorage();
  recoveryCancellation_ = arbiter_.Cancellation();
}
bool AsyncCommandJournal::Resolve(uint8_t readyMask) {
  Request request;
  request.operation = JournalStorageOwner::Operation::Resolve; request.readyMask = readyMask;
  return Apply(request, Purpose::Recovery);
}
bool AsyncCommandJournal::ResolveRestored(uint8_t slot) {
  Request request;
  request.operation = JournalStorageOwner::Operation::ResolveRestored; request.slot = slot;
  return Apply(request, Purpose::Evidence);
}
bool AsyncCommandJournal::ConfirmTimedPresentation(const std::string& id, uint8_t slot, uint64_t fingerprint) {
  return Identified(JournalStorageOwner::Operation::ConfirmTimed, Purpose::Evidence, 0, id, slot, fingerprint);
}
bool AsyncCommandJournal::ConfirmTimedPresentationScoped(uint64_t epoch, const std::string& id,
    uint8_t slot, uint64_t fingerprint) {
  return Identified(JournalStorageOwner::Operation::ConfirmTimed, Purpose::Evidence, epoch, id, slot, fingerprint);
}
bool AsyncCommandJournal::PersistReadyMask(uint8_t readyMask) {
  Request request;
  request.operation = JournalStorageOwner::Operation::Reload; request.readyMask = readyMask;
  return Apply(request, Purpose::Admission);
}

#pragma once

#include <array>
#include <atomic>
#include <mutex>

#include "JournalSnapshot.h"
#include "JournalAdmissionGate.h"
#include "JournalTransactionArbiter.h"

class PersistentCommandJournal;

// One immutable request and one preallocated result slot. The worker alone
// accesses the mutable journal; control reads fixed publications and never waits
// for storage. Inventory intents join this owner before production activation.
class JournalStorageOwner final {
 public:
  using Purpose = JournalTransactionArbiter::Purpose;
  using Token = JournalTransactionArbiter::Token;
  enum class Operation : uint8_t { Begin, Reserve, BeginReserved, Resolve,
    ResolveRestored, ConfirmTimed, Reload };
  enum class Status : uint8_t { Succeeded, Refused, Unknown, CancelledBeforeWrite };
  enum class Submission : uint8_t { Accepted, Busy, Rejected, Unknown };
  struct Request {
    Operation operation = Operation::Begin;
    uint64_t epoch = 0;
    std::array<char, 37> id{};
    uint8_t slot = 0, readyMask = 0;
    uint64_t fingerprint = 0;
  };
  struct Completion {
    Token token;
    Status status = Status::Unknown;
    JournalSnapshot snapshot;
    uint32_t publicationCancellation = 0;
    // Current is cancellation freshness only, never motion/completion proof.
    bool current = false;
  };

  JournalStorageOwner(PersistentCommandJournal& journal, JournalTransactionArbiter& arbiter,
                      JournalAdmissionGate* admission = nullptr);
  JournalStorageOwner(const JournalStorageOwner&) = delete;
  JournalStorageOwner& operator=(const JournalStorageOwner&) = delete;
  bool TrySubmit(const Request& request, Purpose purpose, Token& token);
  Submission TrySubmitTyped(const Request& request, Purpose purpose, Token& token);
  // Call only from the single storage worker. No callback actuates hardware.
  bool RunOne();
  bool TryReadCompletion(const Token& token, Completion& completion) const;
  // The consumer must adopt durable consequences BEFORE releasing the slot.
  // A stale success still contains a committed tombstone/pending operation.
  bool ReleaseCompletion(const Token& token);
  bool TryReadPublished(JournalSnapshot& snapshot, uint32_t* cancellation = nullptr) const;
  void CancelAdmissions() { arbiter_.CancelAdmissions(); }
  bool Busy() const;
  bool HasStorageFault() const { return admission_ && !admission_->AllowsRecovery(); }
  // Single worker only, after its quiescent reconciliation changed the core.
  bool RefreshPublication();
  bool ReconcileBeforeResume(uint32_t cancellation) {
    return !admission_ || admission_->ReconcileBeforeResume(cancellation);
  }
  InspectedRecoveryProgress AdvanceRecoveryResume(uint32_t cancellation) {
    return admission_ ? admission_->AdvanceRecoveryResume(cancellation) : InspectedRecoveryProgress::Succeeded;
  }

 private:
  enum class Phase : uint8_t { Idle, Queued, Running, Completed };
  static bool Same(const Token& left, const Token& right);
  static bool ValidRequest(const Request& request, Purpose purpose);
  bool Execute(const Request& request);
  bool ObserveCancellation();

  PersistentCommandJournal& journal_;
  JournalTransactionArbiter& arbiter_;
  JournalAdmissionGate* admission_;
  mutable std::mutex mutex_;
  Phase phase_ = Phase::Idle;
  Request request_;
  Completion result_;
  JournalSnapshot published_;
  uint32_t observedCancellation_ = 0; // Storage worker only after construction.
  uint32_t publishedCancellation_ = 0;
};

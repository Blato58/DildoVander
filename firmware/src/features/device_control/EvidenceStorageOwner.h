#pragma once

#include <array>
#include <atomic>
#include <memory>
#include <mutex>

#include "JournalTransactionArbiter.h"
#include "../keepsake/KeepsakeOutbox.h"
#include "../offline_show/OfflineShow.h"

class PersistentCommandJournal;
class KeepsakeCommittedInventory;
class JournalAdmissionGate;
namespace kufr { namespace offline { class OfflineCommittedInventory; } }

// Journal and evidence jobs share one storage worker and one arbiter. The
// mailbox owns its result until the consumer adopts even stale durable effects.
class EvidenceStorageOwner final {
 public:
  using Token = JournalTransactionArbiter::Token;
  enum class Operation : uint8_t { OnlinePrepare, OnlineComplete, OnlineRetire,
    OnlineAck, OfflineReserve, OfflineComplete, OfflineAck, OfflineSettings, OfflineEnabled };
  enum class Status : uint8_t { Succeeded, Refused, Unknown, CancelledBeforeWrite };
  struct OnlineOutcome {
    std::array<char, 37> operationId{}, outcomeId{}, proposalId{}, physicalOperationId{}, bootId{};
    uint64_t generation = 0, deviceRevision = 0, retryAtMs = 0;
    uint32_t retryDelayMs = 1000;
    uint8_t compartment = 0;
    uint64_t operationEpoch = 0, physicalFingerprint = 0;
  };
  struct Request {
    Operation operation = Operation::OnlinePrepare;
    OnlineOutcome online;
    kufr::offline::Outcome offline;
    std::array<char, 65> reason{};
    bool setQuiet = false, quiet = false, setAmbient = false, ambient = false;
    bool setEnabled = false, enabled = false;
    uint64_t serverSettingsRevision = 0, serverStopRevision = 0;
    bool fenceStopRevision = false;
    uint64_t expectedStopRevision = 0;
  };
  struct Completion {
    Token token;
    Status status = Status::Unknown;
    uint64_t onlineGeneration = 0, offlineGeneration = 0;
    bool current = false;
  };
  // Terminal idempotent success proves only the persisted disposition tuple
  // (operation/outcome/physical identity/reason). It adds no proof about envelope
  // metadata absent from that ledger, and performs no write or generation bump.

  EvidenceStorageOwner(PersistentCommandJournal& journal, KeepsakeCommittedInventory& online,
      kufr::offline::OfflineCommittedInventory& offline, JournalTransactionArbiter& arbiter,
      JournalAdmissionGate* gate = nullptr);
  EvidenceStorageOwner(const EvidenceStorageOwner&) = delete;
  EvidenceStorageOwner& operator=(const EvidenceStorageOwner&) = delete;
  static bool MakeOnlineRequest(Operation operation, const KeepsakeOutcome& outcome,
      Request& request, const char* reason = "");
  static bool MakeOfflineRequest(Operation operation, const kufr::offline::Outcome& outcome, Request& request);
  bool TrySubmit(const Request& request, Token& token);
  // Worker-only: explicit reopen is required after failed/lost acknowledgements.
  // Neither construction nor opening establishes or repairs service binding.
  bool OpenAndPublish();
  // Startup worker only, under the common exclusive barrier. Promote only
  // exact historical timed proof; retain every uncertain prepared reference.
  bool RecoverProvenCompletions();
  bool RunOne();
  bool TryReadCompletion(const Token& token, Completion& completion) const;
  bool ReleaseCompletion(const Token& token);
  // Online copies allocate and must run off the control thread. The mailbox
  // lock protects only the immutable publication pointer, never that copy.
  bool TryReadOnline(KeepsakeOutboxState& state, uint64_t* generation = nullptr) const;
  bool TryReadOffline(kufr::offline::Outbox& state, uint64_t* generation = nullptr) const;
  bool IsHealthy() const { return onlineAvailable_.load() && offlineAvailable_.load(); }
  bool Busy() const;

 private:
  enum class Phase : uint8_t { Idle, Queued, Running, Completed };
  static bool Same(const Token& left, const Token& right);
  static bool OnlineOperation(Operation operation);
  static bool ValidRequest(const Request& request);
  bool Execute(const Request& request, const Token& token);
  bool ExecuteOnline(const Request& request, const Token& token);
  bool ExecuteOffline(const Request& request, const Token& token);
  bool CanCreateReference(uint64_t epoch, const std::string& id, uint8_t slot,
      uint64_t fingerprint, const Token& token) const;
  bool ReadOnline(KeepsakeOutboxState& state, uint64_t& generation);
  bool ReadOffline(kufr::offline::Outbox& state);
  bool CommitOnline(const KeepsakeOutboxState& state, const Token* admission = nullptr);
  bool CommitOffline(const kufr::offline::Outbox& state, const Token* admission = nullptr);
  bool GateAllows(JournalTransactionArbiter::Purpose purpose) const;
  void UnpublishOnline();
  void UnpublishOffline();

  PersistentCommandJournal& journal_;
  KeepsakeCommittedInventory& online_;
  kufr::offline::OfflineCommittedInventory& offline_;
  JournalTransactionArbiter& arbiter_;
  JournalAdmissionGate* gate_;
  mutable std::mutex mutex_;
  Phase phase_ = Phase::Idle;
  Request request_;
  Completion result_;
  std::shared_ptr<const KeepsakeOutboxState> onlinePublished_;
  kufr::offline::Outbox offlinePublished_;
  uint64_t onlineGeneration_ = 0, offlineGeneration_ = 0;
  std::atomic<bool> onlineAvailable_{false}, offlineAvailable_{false};
};

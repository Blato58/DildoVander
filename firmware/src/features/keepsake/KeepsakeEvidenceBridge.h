#pragma once

#include <array>
#include <cstdint>
#include <mutex>

#include "../device_control/EvidenceStorageOwner.h"
#include "../device_control/JournalAdmissionGate.h"

// RAM handoff only. One network task owns Bootstrap/Pump/LookupPending; the
// storage runtime alone owns durable execution. Construct a new bridge only
// after authoritative runtime recovery, never to clear an uncertain result.
class KeepsakeEvidenceBridge final {
 public:
  using Outcome = EvidenceStorageOwner::OnlineOutcome;
  static constexpr size_t Capacity = KeepsakeOutbox::Capacity;
  struct Handle {
    uint8_t slot = 0;
    uint64_t generation = 0;
  };
  enum class PrepareResult : uint8_t { Busy, Pending, Refused };
  enum class PrepareStatus : uint8_t {
    Pending, DurablePrepared, Refused, Unknown, CancelledBeforeWrite
  };
  enum class Transfer : uint8_t { Busy, RetainedVolatile, AlreadyRetained, Rejected };
  struct Pending {
    Handle handle;
    Outcome outcome;
    uint64_t publicationGeneration = 0;
  };

  KeepsakeEvidenceBridge(EvidenceStorageOwner& owner, JournalAdmissionGate& gate);
  KeepsakeEvidenceBridge(const KeepsakeEvidenceBridge&) = delete;
  KeepsakeEvidenceBridge& operator=(const KeepsakeEvidenceBridge&) = delete;

  // Control-safe: try-locks and fixed copies only. A nonzero handle owns the
  // original tuple even while the shared owner mailbox is busy. Busy means
  // retry this exact call; it never authorizes dropping a completion/retirement.
  PrepareResult TryPrepare(const Outcome& outcome, Handle& handle);
  // DurablePrepared records a current owner completion at adoption. The caller
  // still rechecks its original reservation, TTL and runtime fence before motion.
  // Refused can retain a late durable row until its retirement is adopted.
  PrepareStatus PollPrepare(Handle handle) const;
  Transfer QueueComplete(Handle handle);
  Transfer QueueRetire(Handle handle, const char* reason);
  // Once a terminal intent is retained (including automatic late-prepare
  // cancellation), control may drop its handle without changing that intent.
  // Network releases capacity after terminal adoption.
  // Retrying this handoff is safe while the handle still owns its slot.
  bool TransferToNetwork(Handle handle);
  // Explicit adoption of a terminal result; pending/prepared/unknown cannot
  // release capacity. Stale handles never address a reused slot.
  bool Release(Handle handle);

  // Network-only. Copies of immutable source publications may allocate and
  // always occur outside the metadata lock. These methods never perform IO.
  bool Bootstrap();
  bool Pump();
  bool LookupPending(uint64_t nowMs, Pending& pending);
  // Caller must first validate the HTTP acknowledgement using the existing
  // strict parser. The exact selected tuple is checked again here.
  Transfer QueueValidatedAck(const Pending& acknowledged);
  // Volatile monotonic retry schedule, matching legacy behavior. Reboot resets
  // it; source evidence and the original candidate envelope remain unchanged.
  Transfer QueueDeferred(const Pending& failed, uint64_t nowMs);
  // Network-only exact query; a short metadata lock distinguishes actual
  // poison from ordinary contention with nonblocking control calls.
  bool IsPoisoned() const;
  size_t Occupied() const;

 private:
  using Operation = EvidenceStorageOwner::Operation;
  struct Slot {
    Outcome outcome;
    std::array<char, 65> reason{};
    uint64_t generation = 0;
    uint64_t retryAtMs = 0;
    uint32_t retryDelayMs = 1000;
    PrepareStatus prepare = PrepareStatus::Pending;
    bool occupied = false, networkOwned = false, conservative = false;
    bool prepareIntent = false, completeIntent = false, retireIntent = false, ackIntent = false;
    bool publishedPrepared = false, publishedPending = false, terminal = false, revoked = false;
    bool terminalIntentRetained = false, completeRetained = false;
  };
  struct Flight {
    EvidenceStorageOwner::Token token;
    Handle handle;
    Operation operation = Operation::OnlinePrepare;
    bool adopted = false;
  };
  static bool Valid(const Outcome& outcome);
  static bool Same(const Outcome& left, const Outcome& right);
  static bool SameIdentity(const Outcome& left, const Outcome& right);
  static bool CopyReason(const char* reason, std::array<char, 65>& target);
  static bool Fixed(const KeepsakeOutcome& source, Outcome& target);
  static bool Matches(const KeepsakeOutcome& source, const Outcome& target);
  static bool Disposed(const KeepsakeOutboxState& state, const Slot& slot, const char* reason);
  Slot* Find(Handle handle);
  const Slot* Find(Handle handle) const;
  bool Import(const KeepsakeOutboxState& state);
  void Refresh(const KeepsakeOutboxState& state);
  bool Adopt(const EvidenceStorageOwner::Completion& result, const KeepsakeOutboxState& state);
  bool Submit();
  void Poison();
  bool InFlight(Handle handle) const;
  static void Clear(Slot& slot);

  EvidenceStorageOwner& owner_;
  JournalAdmissionGate& gate_;
  mutable std::mutex mutex_;
  std::array<Slot, Capacity> slots_{};
  Flight flight_;
  size_t next_ = 0;
  bool bootstrapped_ = false, poisoned_ = false;
};

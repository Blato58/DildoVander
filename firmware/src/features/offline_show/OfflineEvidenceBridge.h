#pragma once

#include <array>
#include <cstdint>

#include "../device_control/EvidenceStorageOwner.h"
#include "../device_control/JournalAdmissionGate.h"

namespace kufr { namespace offline {

// All methods belong to one CONTROL task. Only the storage runtime executes
// durable jobs. Requests, publications and retained results are fixed RAM.
class OfflineEvidenceBridge final {
 public:
  static constexpr size_t Capacity = 8;
  struct Handle { uint8_t slot = 0; uint64_t generation = 0; };
  enum class Transfer : uint8_t { Busy, RetainedVolatile, AlreadyRetained, Rejected };
  enum class Status : uint8_t { Pending, Succeeded, Refused, Unknown, CancelledBeforeWrite };
  enum class ReserveStatus : uint8_t { Pending, DurableReserved, Refused, Unknown, CancelledBeforeWrite };
  enum class Lane : uint8_t { Stop, Settings, Enabled };
  struct IntentHandle { Lane lane = Lane::Stop; uint8_t slot = 0; uint64_t generation = 0; };

  OfflineEvidenceBridge(EvidenceStorageOwner& owner, JournalAdmissionGate& gate);
  OfflineEvidenceBridge(const OfflineEvidenceBridge&) = delete;
  OfflineEvidenceBridge& operator=(const OfflineEvidenceBridge&) = delete;
  bool Bootstrap();
  // Adopts one publication/result and accepts at most one owner job. Busy never
  // consumes a retained intent. Call even after Stop or source poisoning.
  bool Pump();
  bool Read(Outbox& outbox) const;
  bool Find(const Outcome& published, Handle& handle) const;

  // The caller first obtains the exact journal reservation ACK. This method
  // reserves bridge capacity before retaining its evidence submission intent.
  Transfer Reserve(const Outcome& original, Handle& handle);
  // DurableReserved caches a current original job ACK, not a renewed motion
  // capability. The producer still checks its claim, runtime fence and deadline.
  ReserveStatus PollReserve(Handle handle) const;
  Transfer Complete(Handle handle, uint64_t completedAtMs);
  Status PollComplete(Handle handle) const;
  Transfer Ack(Handle handle, const Outcome& publishedCompleted);
  Status PollAck(Handle handle) const;
  // Cancel revokes live authority, never a durable obligation or accepted job.
  bool Cancel(Handle handle);
  bool Release(Handle handle);

  // Two entries per lane: immutable current + retained next request. A third
  // distinct request returns Busy for the caller to retain and retry.
  Transfer TryQueueSettings(Lane lane, const EvidenceStorageOwner::Request& request, IntentHandle& handle);
  Status PollSettings(IntentHandle handle) const;
  bool ReleaseSettings(IntentHandle handle);
  bool IsPoisoned() const { return poisoned_; }
  bool HasWork() const;
  size_t Occupied() const;

 private:
  using Operation = EvidenceStorageOwner::Operation;
  struct Candidate {
    Outcome original, published, acknowledged;
    uint64_t generation = 0, completedAtMs = 0;
    ReserveStatus reserve = ReserveStatus::Pending;
    Status complete = Status::Pending, ack = Status::Pending;
    bool occupied = false, present = false, conservative = false, revoked = false, terminal = false;
    bool reserveIntent = false, completeIntent = false, ackIntent = false, completionCaptured = false;
  };
  struct SettingsIntent {
    EvidenceStorageOwner::Request request;
    uint64_t generation = 0, sequence = 0;
    Status status = Status::Pending;
    bool occupied = false, pending = false;
  };
  struct Flight {
    EvidenceStorageOwner::Token token;
    Handle candidate;
    IntentHandle settings;
    Operation operation = Operation::OfflineReserve;
    bool isSettings = false, adopted = false;
  };
  static bool Valid(const Outcome& outcome);
  static bool Empty(const Outcome& outcome);
  static bool SameIdentity(const Outcome& left, const Outcome& right);
  static bool Same(const Outcome& left, const Outcome& right);
  static bool SameRequest(const EvidenceStorageOwner::Request& left, const EvidenceStorageOwner::Request& right);
  static bool ValidSettings(Lane lane, const EvidenceStorageOwner::Request& request);
  static bool Relaxing(const EvidenceStorageOwner::Request& request);
  static bool MatchesSettings(const EvidenceStorageOwner::Request& request, const Outbox& state);
  static Status Convert(EvidenceStorageOwner::Status status);
  Candidate* Get(Handle handle);
  const Candidate* Get(Handle handle) const;
  SettingsIntent* Get(IntentHandle handle);
  const SettingsIntent* Get(IntentHandle handle) const;
  bool InFlight(Handle handle) const;
  bool InFlight(IntentHandle handle) const;
  bool Refresh(const Outbox& state, uint64_t generation);
  void Poison();
  void Adopt(const EvidenceStorageOwner::Completion& result);
  bool SubmitCandidate(size_t index);
  bool SubmitSettings(Lane lane);
  bool Submit();

  EvidenceStorageOwner& owner_;
  JournalAdmissionGate& gate_;
  std::array<Candidate, Capacity> candidates_{};
  std::array<std::array<SettingsIntent, 2>, 3> settings_{};
  Outbox publication_{};
  uint64_t publicationGeneration_ = 0, nextSequence_ = 0;
  size_t nextProducer_ = 0;
  Flight flight_;
  bool bootstrapped_ = false, poisoned_ = false;
};

} }

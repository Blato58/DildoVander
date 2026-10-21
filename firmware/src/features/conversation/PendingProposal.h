#pragma once

#include <stdint.h>

#include <mutex>

#include "ConversationRuntimeState.h"
#include "TurnPollGuard.h"

// Data handoff to the future autonomous presentation owner. It cannot move
// hardware and becomes unreadable after a snapshot change or local deadline.
class PendingProposal final {
 public:
  // A one-shot control-owner handoff. Runtime changes cannot rewrite the
  // original intent, and claiming never renews its original local deadline.
  class ClaimedProposal final {
   public:
    ClaimedProposal() = default;
    ClaimedProposal(const ClaimedProposal&) = delete;
    ClaimedProposal& operator=(const ClaimedProposal&) = delete;
    ClaimedProposal(ClaimedProposal&& other) noexcept;
    ClaimedProposal& operator=(ClaimedProposal&& other) noexcept;

    const TurnProposal& Proposal() const { return proposal_; }
    const ConversationRuntimeSnapshot& Snapshot() const { return snapshot_; }
    bool IsLive(uint64_t nowMs) const;
    uint64_t StoredAtMs() const { return storedAtMs_; }
    uint64_t DeadlineMs() const { return deadlineMs_; }
    bool IsClaimed() const { return claimed_; }
    void Clear();

   private:
    friend class PendingProposal;
    TurnProposal proposal_;
    ConversationRuntimeSnapshot snapshot_;
    uint64_t deadlineMs_ = 0;
    uint64_t storedAtMs_ = 0;
    bool claimed_ = false;
  };

  bool Store(const TurnProposal& proposal,
             const ConversationRuntimeSnapshot& snapshot,
             const ConversationRuntimeState& runtimeState,
             uint64_t nowMs, uint64_t remainingMs);
  bool Read(TurnProposal& proposal,
            const ConversationRuntimeState& runtimeState,
            uint64_t nowMs) const;
  bool Consume(TurnProposal& proposal,
               ConversationRuntimeSnapshot& snapshot,
               const ConversationRuntimeState& runtimeState,
               uint64_t nowMs);
  bool Claim(ClaimedProposal& claimed,
             const ConversationRuntimeState& runtimeState,
             uint64_t nowMs);
  void Clear();

 private:
  mutable std::mutex mutex_;
  TurnProposal proposal_;
  ConversationRuntimeSnapshot snapshot_;
  uint64_t deadlineMs_ = 0;
  uint64_t storedAtMs_ = 0;
  bool pending_ = false;
};

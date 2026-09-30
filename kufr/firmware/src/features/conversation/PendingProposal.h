#pragma once

#include <stdint.h>

#include <mutex>

#include "ConversationRuntimeState.h"
#include "TurnPollGuard.h"

// Data handoff to the future autonomous presentation owner. It cannot move
// hardware and becomes unreadable after a snapshot change or local deadline.
class PendingProposal final {
 public:
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
  void Clear();

 private:
  mutable std::mutex mutex_;
  TurnProposal proposal_;
  ConversationRuntimeSnapshot snapshot_;
  uint64_t deadlineMs_ = 0;
  uint64_t storedAtMs_ = 0;
  bool pending_ = false;
};

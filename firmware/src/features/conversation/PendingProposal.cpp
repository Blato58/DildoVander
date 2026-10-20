#include "PendingProposal.h"

bool PendingProposal::Store(
    const TurnProposal& proposal,
    const ConversationRuntimeSnapshot& snapshot,
    const ConversationRuntimeState& runtimeState,
    uint64_t nowMs, uint64_t remainingMs) {
  if (remainingMs == 0 || remainingMs > 60000 ||
      nowMs > UINT64_MAX - remainingMs ||
      proposal.proposalId.empty() ||
      proposal.compartment < 1 || proposal.compartment > 6 ||
      proposal.catalogVersion != snapshot.catalogVersion ||
      proposal.generation != snapshot.generation ||
      proposal.deviceRevision != snapshot.deviceRevision ||
      (snapshot.readyMask & (1u << (proposal.compartment - 1))) == 0 ||
      !runtimeState.Matches(snapshot)) {
    return false;
  }
  std::lock_guard<std::mutex> lock(mutex_);
  if (!runtimeState.Matches(snapshot)) {
    return false;
  }
  proposal_ = proposal;
  snapshot_ = snapshot;
  deadlineMs_ = nowMs + remainingMs;
  storedAtMs_ = nowMs;
  pending_ = true;
  return true;
}

bool PendingProposal::Read(
    TurnProposal& proposal,
    const ConversationRuntimeState& runtimeState,
    uint64_t nowMs) const {
  std::lock_guard<std::mutex> lock(mutex_);
  if (!pending_ || nowMs < storedAtMs_ || nowMs >= deadlineMs_ ||
      !runtimeState.Matches(snapshot_)) {
    return false;
  }
  proposal = proposal_;
  return true;
}

bool PendingProposal::Consume(
    TurnProposal& proposal,
    ConversationRuntimeSnapshot& snapshot,
    const ConversationRuntimeState& runtimeState,
    uint64_t nowMs) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (!pending_ || nowMs < storedAtMs_ || nowMs >= deadlineMs_ ||
      !runtimeState.Matches(snapshot_)) {
    return false;
  }
  proposal = proposal_;
  snapshot = snapshot_;
  proposal_ = {};
  snapshot_ = {};
  deadlineMs_ = 0;
  storedAtMs_ = 0;
  pending_ = false;
  return true;
}

void PendingProposal::Clear() {
  std::lock_guard<std::mutex> lock(mutex_);
  proposal_ = {};
  snapshot_ = {};
  deadlineMs_ = 0;
  storedAtMs_ = 0;
  pending_ = false;
}

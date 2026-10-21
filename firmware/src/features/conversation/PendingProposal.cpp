#include "PendingProposal.h"

#include <utility>

PendingProposal::ClaimedProposal::ClaimedProposal(
    ClaimedProposal&& other) noexcept {
  *this = std::move(other);
}

PendingProposal::ClaimedProposal&
PendingProposal::ClaimedProposal::operator=(ClaimedProposal&& other) noexcept {
  if (this != &other) {
    proposal_ = std::move(other.proposal_);
    snapshot_ = std::move(other.snapshot_);
    deadlineMs_ = other.deadlineMs_;
    storedAtMs_ = other.storedAtMs_;
    claimed_ = other.claimed_;
    other.Clear();
  }
  return *this;
}

bool PendingProposal::ClaimedProposal::IsLive(uint64_t nowMs) const {
  return claimed_ && nowMs >= storedAtMs_ && nowMs < deadlineMs_;
}

void PendingProposal::ClaimedProposal::Clear() {
  proposal_ = {};
  snapshot_ = {};
  deadlineMs_ = 0;
  storedAtMs_ = 0;
  claimed_ = false;
}

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
      proposal.operationEpoch != snapshot.operationEpoch ||
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

bool PendingProposal::Claim(
    ClaimedProposal& claimed,
    const ConversationRuntimeState& runtimeState,
    uint64_t nowMs) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (claimed.IsClaimed() || !pending_ || nowMs < storedAtMs_ ||
      nowMs >= deadlineMs_ || !runtimeState.Matches(snapshot_)) {
    return false;
  }
  claimed.proposal_ = std::move(proposal_);
  claimed.snapshot_ = std::move(snapshot_);
  claimed.deadlineMs_ = deadlineMs_;
  claimed.storedAtMs_ = storedAtMs_;
  claimed.claimed_ = true;
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

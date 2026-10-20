#include <cassert>
#include <thread>
#include <type_traits>
#include <utility>

#include "../src/features/conversation/ConversationRuntimeState.h"
#include "../src/features/conversation/PendingProposal.h"

namespace {

static_assert(!std::is_copy_constructible<PendingProposal::ClaimedProposal>::value,
              "An owned proposal must not be copied");
static_assert(!std::is_copy_assignable<PendingProposal::ClaimedProposal>::value,
              "An owned proposal must not be copied");
static_assert(std::is_same<
                  decltype(std::declval<PendingProposal::ClaimedProposal&>()
                               .Proposal()),
                  const TurnProposal&>::value,
              "An owned proposal must be immutable");
static_assert(std::is_same<
                  decltype(std::declval<PendingProposal::ClaimedProposal&>()
                               .Snapshot()),
                  const ConversationRuntimeSnapshot&>::value,
              "An owned snapshot must be immutable");

TurnProposal MakeProposal(const ConversationRuntimeSnapshot& snapshot,
                          const char* id) {
  TurnProposal proposal;
  proposal.proposalId = id;
  proposal.conversationId = "conversation";
  proposal.turnId = "turn";
  proposal.catalogVersion = snapshot.catalogVersion;
  proposal.bootId = "boot";
  proposal.reason = "original reason";
  proposal.expiresAt = "2026-10-20T12:00:00Z";
  proposal.generation = snapshot.generation;
  proposal.deviceRevision = snapshot.deviceRevision;
  proposal.compartment = 3;
  proposal.operationEpoch = snapshot.operationEpoch;
  return proposal;
}

void AssertProposal(const TurnProposal& actual, const TurnProposal& expected) {
  assert(actual.proposalId == expected.proposalId);
  assert(actual.conversationId == expected.conversationId);
  assert(actual.turnId == expected.turnId);
  assert(actual.catalogVersion == expected.catalogVersion);
  assert(actual.bootId == expected.bootId);
  assert(actual.reason == expected.reason);
  assert(actual.expiresAt == expected.expiresAt);
  assert(actual.generation == expected.generation);
  assert(actual.deviceRevision == expected.deviceRevision);
  assert(actual.compartment == expected.compartment);
  assert(actual.operationEpoch == expected.operationEpoch);
}

void AssertSnapshot(const ConversationRuntimeSnapshot& actual,
                    const ConversationRuntimeSnapshot& expected) {
  assert(actual.generation == expected.generation);
  assert(actual.deviceRevision == expected.deviceRevision);
  assert(actual.version == expected.version);
  assert(actual.readyMask == expected.readyMask);
  assert(actual.catalogVersion == expected.catalogVersion);
  assert(actual.operationEpoch == expected.operationEpoch);
}

void TestLegacyReadAndConsume() {
  ConversationRuntimeState runtime(true, 63, "neutral-1");
  ConversationRuntimeSnapshot snapshot;
  assert(runtime.Read(snapshot));
  PendingProposal pending;
  TurnProposal proposal;
  proposal.proposalId = "first";
  proposal.compartment = 3;
  proposal.catalogVersion = snapshot.catalogVersion;
  proposal.generation = snapshot.generation;
  proposal.deviceRevision = snapshot.deviceRevision;
  assert(pending.Store(proposal, snapshot, runtime, 1000, 29000));
  TurnProposal visible;
  assert(!pending.Read(visible, runtime, 999));
  assert(pending.Read(visible, runtime, 1001));
  assert(visible.compartment == 3);
  assert(!pending.Read(visible, runtime, 30000));
  assert(runtime.Publish(0, 1, 63, "neutral-1"));
  assert(!pending.Read(visible, runtime, 1002));
  pending.Clear();
  assert(!pending.Read(visible, runtime, 1002));

  assert(runtime.Read(snapshot));
  proposal = MakeProposal(snapshot, "consume");
  assert(pending.Store(proposal, snapshot, runtime, 2000, 1000));
  ConversationRuntimeSnapshot consumedSnapshot;
  assert(pending.Consume(visible, consumedSnapshot, runtime, 2001));
  AssertProposal(visible, proposal);
  AssertSnapshot(consumedSnapshot, snapshot);
  assert(!pending.Read(visible, runtime, 2001));
  assert(!pending.Consume(visible, consumedSnapshot, runtime, 2001));
}

void TestClaimPreservesOriginalIntentAndDeadline() {
  ConversationRuntimeState runtime(true, 63, "neutral-1");
  assert(runtime.Publish(7, 11, 63, "neutral-1", 13));
  ConversationRuntimeSnapshot snapshot;
  assert(runtime.Read(snapshot));
  const TurnProposal proposal = MakeProposal(snapshot, "original");
  PendingProposal pending;
  PendingProposal::ClaimedProposal claimed;
  assert(!claimed.IsClaimed());
  assert(!claimed.IsLive(0));
  assert(pending.Store(proposal, snapshot, runtime, 1000, 29000));
  assert(pending.Claim(claimed, runtime, 29000));
  assert(claimed.IsClaimed());
  assert(claimed.StoredAtMs() == 1000);
  assert(claimed.DeadlineMs() == 30000);
  AssertProposal(claimed.Proposal(), proposal);
  AssertSnapshot(claimed.Snapshot(), snapshot);

  TurnProposal visible;
  ConversationRuntimeSnapshot visibleSnapshot;
  PendingProposal::ClaimedProposal second;
  assert(!pending.Read(visible, runtime, 29000));
  assert(!pending.Consume(visible, visibleSnapshot, runtime, 29000));
  assert(!pending.Claim(second, runtime, 29000));
  runtime.Invalidate();
  pending.Clear();
  AssertProposal(claimed.Proposal(), proposal);
  AssertSnapshot(claimed.Snapshot(), snapshot);
  assert(!claimed.IsLive(999));
  assert(claimed.IsLive(1000));
  assert(claimed.IsLive(29999));
  assert(!claimed.IsLive(30000));
  assert(!claimed.IsLive(UINT64_MAX));
  assert(claimed.StoredAtMs() == 1000);
  assert(claimed.DeadlineMs() == 30000);
}

void TestClaimCannotOverwriteAndNewStoreIsIndependent() {
  ConversationRuntimeState runtime(true, 63, "neutral-1");
  ConversationRuntimeSnapshot snapshot;
  assert(runtime.Read(snapshot));
  const TurnProposal original = MakeProposal(snapshot, "original");
  TurnProposal next = MakeProposal(snapshot, "next");
  next.compartment = 5;
  PendingProposal pending;
  PendingProposal::ClaimedProposal claimed;
  assert(pending.Store(original, snapshot, runtime, 1000, 1000));
  assert(pending.Claim(claimed, runtime, 1001));
  assert(pending.Store(next, snapshot, runtime, 1500, 4000));
  // Even an expired destination retains ownership until explicitly cleared.
  assert(!pending.Claim(claimed, runtime, 2500));
  AssertProposal(claimed.Proposal(), original);
  assert(claimed.DeadlineMs() == 2000);
  TurnProposal visible;
  assert(pending.Read(visible, runtime, 2500));
  AssertProposal(visible, next);
  claimed.Clear();
  assert(!claimed.IsClaimed());
  assert(!claimed.IsLive(1500));
  assert(claimed.StoredAtMs() == 0);
  assert(claimed.DeadlineMs() == 0);
  assert(claimed.Proposal().proposalId.empty());
  assert(claimed.Snapshot().version == 0);
  assert(pending.Claim(claimed, runtime, 2500));
  AssertProposal(claimed.Proposal(), next);
  assert(claimed.StoredAtMs() == 1500);
  assert(claimed.DeadlineMs() == 5500);
}

void TestClaimRejectsInvalidRuntimeAndTime() {
  ConversationRuntimeState runtime(true, 63, "neutral-1");
  ConversationRuntimeSnapshot snapshot;
  assert(runtime.Read(snapshot));
  TurnProposal proposal = MakeProposal(snapshot, "timed");
  PendingProposal pending;
  PendingProposal::ClaimedProposal claimed;
  assert(pending.Store(proposal, snapshot, runtime, 1000, 1000));
  assert(!pending.Claim(claimed, runtime, 999));
  assert(!claimed.IsClaimed());
  assert(pending.Claim(claimed, runtime, 1000));
  claimed.Clear();
  assert(pending.Store(proposal, snapshot, runtime, 1000, 1000));
  assert(!pending.Claim(claimed, runtime, 2000));
  assert(!pending.Claim(claimed, runtime, UINT64_MAX));
  assert(!claimed.IsClaimed());
  assert(claimed.DeadlineMs() == 0);
  assert(pending.Store(proposal, snapshot, runtime, 3000, 1000));
  assert(runtime.Publish(0, 1, 63, "neutral-1"));
  assert(!pending.Claim(claimed, runtime, 3001));
  assert(!claimed.IsClaimed());

  assert(runtime.Read(snapshot));
  proposal = MakeProposal(snapshot, "near-clock-limit");
  assert(pending.Store(proposal, snapshot, runtime, UINT64_MAX - 1000, 1000));
  assert(pending.Claim(claimed, runtime, UINT64_MAX - 1));
  assert(claimed.DeadlineMs() == UINT64_MAX);
  assert(claimed.IsLive(UINT64_MAX - 1));
  assert(!claimed.IsLive(UINT64_MAX));
}

void TestMoveInvalidatesSource() {
  ConversationRuntimeState runtime(true, 63, "neutral-1");
  ConversationRuntimeSnapshot snapshot;
  assert(runtime.Read(snapshot));
  const TurnProposal proposal = MakeProposal(snapshot, "moved");
  PendingProposal pending;
  PendingProposal::ClaimedProposal source;
  assert(pending.Store(proposal, snapshot, runtime, 1000, 1000));
  assert(pending.Claim(source, runtime, 1001));
  PendingProposal::ClaimedProposal moved(std::move(source));
  assert(!source.IsClaimed());
  assert(!source.IsLive(1001));
  assert(source.Proposal().proposalId.empty());
  assert(source.DeadlineMs() == 0);
  assert(moved.IsLive(1999));
  AssertProposal(moved.Proposal(), proposal);
  AssertSnapshot(moved.Snapshot(), snapshot);
  PendingProposal::ClaimedProposal assigned;
  assigned = std::move(moved);
  assert(!moved.IsClaimed());
  assert(!moved.IsLive(1001));
  assert(moved.Snapshot().version == 0);
  assert(assigned.IsLive(1999));
  assert(!assigned.IsLive(2000));
  assert(assigned.StoredAtMs() == 1000);
  assert(assigned.DeadlineMs() == 2000);
  AssertProposal(assigned.Proposal(), proposal);
  AssertSnapshot(assigned.Snapshot(), snapshot);
}

void TestConcurrentClaimHasOneOwner() {
  ConversationRuntimeState runtime(true, 63, "neutral-1");
  ConversationRuntimeSnapshot snapshot;
  assert(runtime.Read(snapshot));
  const TurnProposal proposal = MakeProposal(snapshot, "concurrent");
  PendingProposal pending;
  assert(pending.Store(proposal, snapshot, runtime, 1000, 1000));
  PendingProposal::ClaimedProposal first;
  PendingProposal::ClaimedProposal second;
  bool firstClaimed = false;
  bool secondClaimed = false;
  std::thread firstThread([&]() {
    firstClaimed = pending.Claim(first, runtime, 1001);
  });
  std::thread secondThread([&]() {
    secondClaimed = pending.Claim(second, runtime, 1001);
  });
  firstThread.join();
  secondThread.join();
  assert(firstClaimed != secondClaimed);
  assert(first.IsClaimed() == firstClaimed);
  assert(second.IsClaimed() == secondClaimed);
  const auto& owned = firstClaimed ? first : second;
  AssertProposal(owned.Proposal(), proposal);
  AssertSnapshot(owned.Snapshot(), snapshot);
}

}  // namespace

int main() {
  TestLegacyReadAndConsume();
  TestClaimPreservesOriginalIntentAndDeadline();
  TestClaimCannotOverwriteAndNewStoreIsIndependent();
  TestClaimRejectsInvalidRuntimeAndTime();
  TestMoveInvalidatesSource();
  TestConcurrentClaimHasOneOwner();
}

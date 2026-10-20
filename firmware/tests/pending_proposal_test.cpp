#include <cassert>

#include "../src/features/conversation/ConversationRuntimeState.h"
#include "../src/features/conversation/PendingProposal.h"

int main() {
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
}

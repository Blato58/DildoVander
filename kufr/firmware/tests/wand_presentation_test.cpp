#include <cassert>

#include "../src/features/wand_presentation/WandPresentation.h"

namespace {

class FakePort final : public WandPresentationPort {
 public:
  bool ready = true;
  bool safe = true;
  bool raised = false;
  bool resting = true;
  int raises = 0;
  int lowers = 0;
  int completes = 0;
  int faults = 0;

  bool IsReady(uint8_t) const override { return ready; }
  bool IsSafeToContinue() const override { return safe; }
  bool IsRaised(uint8_t) const override { return raised; }
  bool IsResting(uint8_t) const override { return resting; }
  bool Raise(uint8_t, const std::string&,
             const ConversationRuntimeSnapshot&) override {
    ++raises;
    resting = false;
    return true;
  }
  bool Lower(uint8_t) override {
    ++lowers;
    return true;
  }
  bool Complete(uint8_t) override {
    ++completes;
    return true;
  }
  void DisableAndFault() override { ++faults; }
};

TurnProposal Proposal(const std::string& id,
                      const ConversationRuntimeSnapshot& snapshot) {
  TurnProposal proposal;
  proposal.proposalId = id;
  proposal.compartment = 3;
  proposal.catalogVersion = snapshot.catalogVersion;
  proposal.generation = snapshot.generation;
  proposal.deviceRevision = snapshot.deviceRevision;
  return proposal;
}

}  // namespace

int main() {
  ConversationRuntimeState runtime(true, 63, "neutral-1");
  ConversationRuntimeSnapshot snapshot;
  assert(runtime.Read(snapshot));
  PendingProposal pending;
  FakePort port;
  WandPresentation presentation(port);
  assert(pending.Store(Proposal("first", snapshot), snapshot, runtime,
                       1000, 30000));
  assert(port.raises == 0);
  assert(presentation.Start(pending, runtime, 1000));
  assert(port.raises == 1);
  assert(!presentation.Start(pending, runtime, 1001));
  port.raised = true;
  presentation.Tick(1200);
  assert(presentation.State() == WandPresentationState::Showing);
  presentation.Tick(6200);
  assert(presentation.State() == WandPresentationState::Lowering);
  assert(port.lowers == 1);
  port.resting = true;
  presentation.Tick(6300);
  assert(presentation.State() == WandPresentationState::Idle);
  assert(port.completes == 1 && port.faults == 0);
  assert(pending.Store(Proposal("first", snapshot), snapshot, runtime,
                       6400, 30000));
  assert(!presentation.Start(pending, runtime, 6400));
  assert(port.raises == 1);
  pending.Clear();

  assert(pending.Store(Proposal("second", snapshot), snapshot, runtime,
                       7000, 30000));
  assert(presentation.Start(pending, runtime, 7000));
  presentation.Stop();
  assert(presentation.State() == WandPresentationState::Fault);
  assert(port.faults == 1);
  assert(!presentation.Start(pending, runtime, 7001));
  assert(!presentation.RecoverAfterInspection(true));
  port.resting = true;
  assert(!presentation.RecoverAfterInspection(false));
  assert(presentation.RecoverAfterInspection(true));
  assert(presentation.State() == WandPresentationState::Idle);

  assert(pending.Store(Proposal("third", snapshot), snapshot, runtime,
                       8000, 30000));
  assert(presentation.Start(pending, runtime, 8000));
  port.raised = false;
  presentation.Tick(11000);
  assert(presentation.State() == WandPresentationState::Fault);
  assert(port.faults == 2);
  port.resting = true;
  assert(presentation.RecoverAfterInspection(true));
  assert(runtime.Publish(0, 1, 63, "neutral-1"));
  assert(pending.Store(Proposal("fourth", snapshot), snapshot, runtime,
                       12000, 30000) == false);
  assert(port.raises == 3);

  assert(runtime.Read(snapshot));
  assert(pending.Store(Proposal("fifth", snapshot), snapshot, runtime,
                       13000, 30000));
  assert(presentation.Start(pending, runtime, 13000));
  port.safe = false;
  presentation.Tick(13001);
  assert(presentation.State() == WandPresentationState::Fault);
  assert(port.faults == 3);
  port.safe = true;
  port.resting = true;
  assert(presentation.RecoverAfterInspection(true));
  assert(pending.Store(Proposal("sixth", snapshot), snapshot, runtime,
                       14000, 30000));
  assert(presentation.Start(pending, runtime, 14000));
  presentation.Tick(13999);
  assert(presentation.State() == WandPresentationState::Fault);
  assert(port.faults == 4);
  port.resting = true;
  assert(presentation.RecoverAfterInspection(true));
  presentation.Stop();
  assert(presentation.State() == WandPresentationState::Fault);
  assert(port.faults == 5);
  assert(!presentation.RecoverAfterInspection(false));
  assert(presentation.RecoverAfterInspection(true));
}

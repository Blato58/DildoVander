#include <stdint.h>

#include <cstdlib>
#include <fstream>
#include <iostream>
#include <iterator>
#include <string>

#include "../src/features/conversation/TurnOperationCodec.h"
#include "../src/features/wand_presentation/WandPresentation.h"

namespace {

class FakePresentationPort final : public WandPresentationPort {
 public:
  bool IsReady(uint8_t) const override { return true; }
  bool IsSafeToContinue() const override { return true; }
  bool IsRaised(uint8_t) const override { return raised_; }
  bool IsResting(uint8_t) const override { return resting_; }
  bool Raise(uint8_t compartment, const std::string&,
             const ConversationRuntimeSnapshot&) override {
    std::cout << "SIMULATED raise " << static_cast<unsigned>(compartment) << '\n';
    resting_ = false;
    raised_ = true;
    return true;
  }
  bool Lower(uint8_t compartment) override {
    std::cout << "SIMULATED lower " << static_cast<unsigned>(compartment) << '\n';
    resting_ = true;
    return true;
  }
  bool Complete(uint8_t compartment) override {
    std::cout << "SIMULATED complete " << static_cast<unsigned>(compartment) << '\n';
    return true;
  }
  void DisableAndFault() override { std::cout << "SIMULATED fault\n"; }

 private:
  bool raised_ = false;
  bool resting_ = true;
};

uint64_t Unsigned(const char* value) {
  char* end = nullptr;
  const unsigned long long parsed = std::strtoull(value, &end, 10);
  return end != value && *end == '\0' ? parsed : UINT64_MAX;
}

}  // namespace

int main(int argc, char** argv) {
  if (argc != 12) {
    std::cerr << "usage: turn_replay operation.json operationId deviceId bootId "
                 "conversationId clientTurnId generation revision catalogVersion "
                 "readyMask nowUtcSeconds\n";
    return 2;
  }
  std::ifstream input(argv[1], std::ios::binary);
  const std::string json((std::istreambuf_iterator<char>(input)),
                         std::istreambuf_iterator<char>());
  TurnOperationObservation observed;
  if (!input || !ParseTurnOperation(json.data(), json.size(), observed)) {
    std::cerr << "Invalid operation JSON\n";
    return 2;
  }
  TurnCorrelation expected;
  expected.deviceId = argv[3];
  expected.bootId = argv[4];
  expected.conversationId = argv[5];
  expected.clientTurnId = argv[6];
  expected.generation = Unsigned(argv[7]);
  expected.deviceRevision = Unsigned(argv[8]);
  const uint64_t mask = Unsigned(argv[10]);
  const uint64_t nowUtcSeconds = Unsigned(argv[11]);
  if (mask > 63 || nowUtcSeconds == UINT64_MAX ||
      expected.generation == UINT64_MAX ||
      expected.deviceRevision == UINT64_MAX ||
      EvaluateTurnOperationWithProposal(
          argv[2], expected, expected.generation, expected.deviceRevision,
          argv[9], static_cast<uint8_t>(mask), nowUtcSeconds, observed) !=
          TurnPollDecision::ProposalReady) {
    std::cerr << "Proposal rejected\n";
    return 2;
  }
  ConversationRuntimeState runtime(true, static_cast<uint8_t>(mask), argv[9]);
  if (!runtime.Publish(expected.generation, expected.deviceRevision,
                       static_cast<uint8_t>(mask), argv[9])) {
    return 2;
  }
  ConversationRuntimeSnapshot snapshot;
  if (!runtime.Read(snapshot)) {
    return 2;
  }
  PendingProposal pending;
  if (!pending.Store(observed.proposal, snapshot, runtime, 1000,
                     ProposalRemainingMs(observed.proposal, nowUtcSeconds))) {
    return 2;
  }
  FakePresentationPort port;
  WandPresentation presentation(port);
  if (!presentation.Start(pending, runtime, 1000)) {
    return 2;
  }
  presentation.Tick(1001);
  presentation.Tick(6001);
  presentation.Tick(6002);
  return presentation.State() == WandPresentationState::Idle ? 0 : 2;
}

#include <cassert>
#include <string>

#include "../src/features/conversation/ConversationSession.h"
#include "../src/features/conversation/ConversationStartCodec.h"
#include "../src/features/conversation/ConversationTurnFailureInbox.h"
#include "../src/features/device_control/DeviceControlRuntime.h"
#include "../src/features/local_control/LocalControlSession.h"
#include "../src/features/local_control/LocalConversationGateway.h"
#include "../src/features/local_control/LocalConversationNetworkPort.h"
#include "../src/features/safety/SafetyController.h"

namespace {

constexpr char BootId[] = "ac165d39-9f2a-4f3e-a613-3ad676a39d2e";
constexpr char OtherBootId[] = "12f0b2f9-27cc-4f19-b28e-0bd18855961b";
constexpr char LeaseId[] = "287d9ff1-c62b-43b9-8f36-f92de0ae383e";
constexpr char OtherLeaseId[] = "95720438-72b5-4533-bc80-319c6d78181a";
constexpr char ConversationId[] = "f1e83b7c-df66-48df-b0f8-6c7b215b38b1";
constexpr char OtherConversationId[] = "e089a5ad-44fb-4ea5-a802-d14c2e26bcf0";
constexpr char TurnOneId[] = "ad64d2ac-d76a-4e5d-9ce7-a0b468f31c85";
constexpr char TurnTwoId[] = "79455e9c-f38c-4055-a559-4c02e356b2c9";

class FakeSafetyPort final : public SafetyPort {
 public:
  void DisableActuators() override {}
  void StartVibration(uint8_t) override {}
  void ReleaseLatch(uint8_t) override {}
};

class FakeJournal final : public CommandJournal {
 public:
  bool HasPending() const override { return false; }
  uint8_t PendingCompartment() const override { return 0; }
  uint8_t ReadyMask() const override { return 63; }
  bool Begin(const std::string&, uint8_t) override { return false; }
  bool Resolve(uint8_t) override { return false; }
  bool PersistReadyMask(uint8_t) override { return false; }
};

class FakeConversationNetwork final : public LocalConversationNetworkPort {
 public:
  bool QueueTextTurn(
      const std::string& text, const std::string& conversationId,
      const std::string& clientTurnId,
      const ConversationRuntimeSnapshot& snapshot) override {
    ++attempts;
    std::string normalizedId;
    if (!accept || cancelPending ||
        !NormalizeConversationUuid(conversationId, normalizedId) ||
        !NormalizeConversationUuid(clientTurnId, normalizedId)) {
      return false;
    }
    ++queued;
    lastText = text;
    lastConversationId = conversationId;
    lastTurnId = clientTurnId;
    lastSnapshot = snapshot;
    return true;
  }

  bool QueueCancelFor(
      const std::string& conversationId,
      uint64_t snapshotVersion) override {
    if (lastConversationId != conversationId ||
        lastSnapshot.version != snapshotVersion) {
      return false;
    }
    ++cancels;
    cancelPending = true;
    return true;
  }

  bool TakeFailedTurnFor(
      const std::string& conversationId,
      const std::string& clientTurnId,
      uint64_t snapshotVersion) override {
    return failedTurns.TakeFor(
        conversationId, clientTurnId, snapshotVersion);
  }

  void FailLastTurn() {
    failedTurns.Store(
        lastConversationId, lastTurnId, lastSnapshot.version);
  }

  bool accept = true;
  bool cancelPending = false;
  int attempts = 0;
  int queued = 0;
  int cancels = 0;
  std::string lastText;
  std::string lastConversationId;
  std::string lastTurnId;
  ConversationRuntimeSnapshot lastSnapshot;
  ConversationTurnFailureInbox failedTurns;
};

struct Fixture {
  Fixture()
      : safety(port),
        sequence(safety),
        control("kufr-1", BootId, sequence, journal, 1),
        state(false, 0, ""),
        runtime(control, sequence, state, "neutral-1") {
    safety.InitializeSafe();
    assert(runtime.RecoverAfterInspection());
  }

  FakeSafetyPort port;
  SafetyController safety;
  SelectionSequence sequence;
  FakeJournal journal;
  DeviceControl control;
  ConversationRuntimeState state;
  DeviceControlRuntime runtime;
};

void TestObserverDisconnectDoesNotChangeControl() {
  Fixture fixture;
  const uint64_t generation = fixture.runtime.Generation();
  const uint64_t revision = fixture.control.Revision();
  LocalControlSession observer(fixture.runtime, "kufr-1", BootId, 100);
  assert(observer.Hello(1, "kufr-1", 200));
  SelectionStep step;
  observer.Disconnect(step);
  observer.Disconnect(step);
  assert(!fixture.control.HasManualControl());
  assert(fixture.runtime.Generation() == generation);
  assert(fixture.control.Revision() == revision);
  assert(!observer.Acquire(BootId, LeaseId, 300));
}

void TestHelloAndLeaseGuards() {
  Fixture fixture;
  LocalControlSession session(fixture.runtime, "kufr-1", BootId, 100);
  assert(!session.Acquire(BootId, LeaseId, 100));
  assert(!session.Hello(2, "kufr-1", 101));
  assert(!session.Hello(1, "other-device", 101));
  assert(!session.Hello(1, "kufr-1", 10101));
  assert(session.Hello(1, "kufr-1", 10100));
  assert(!session.Hello(1, "kufr-1", 10100));
  assert(!session.Acquire(OtherBootId, LeaseId, 10100));
  assert(!session.Acquire(BootId, "invalid", 10100));
  assert(session.Acquire(BootId, LeaseId, 10100));
  assert(fixture.control.HasManualControl());
  assert(!session.Acquire(BootId, OtherLeaseId, 10101));
  assert(!session.Heartbeat(BootId, OtherLeaseId, 10101));
  assert(!session.Heartbeat(OtherBootId, LeaseId, 10101));
  assert(session.Heartbeat(BootId, LeaseId, 10101));
  SelectionStep step;
  assert(!session.Release(BootId, OtherLeaseId, step));
  assert(session.Release(BootId, LeaseId, step));
  assert(!fixture.control.HasManualControl());
  assert(fixture.runtime.Generation() == 1);
}

void TestExpiryAndOwnerDisconnect() {
  Fixture fixture;
  LocalControlSession session(fixture.runtime, "kufr-1", BootId, 0);
  assert(session.Hello(1, "kufr-1", 0));
  assert(session.Acquire(BootId, LeaseId, 0));
  assert(!session.Heartbeat(BootId, LeaseId, 6000));
  assert(!fixture.control.HasManualControl());
  assert(fixture.runtime.Generation() == 1);
  SelectionStep step;
  session.Disconnect(step);
  assert(fixture.runtime.Generation() == 1);

  LocalControlSession next(fixture.runtime, "kufr-1", BootId, 7000);
  assert(next.Hello(1, "kufr-1", 7000));
  assert(!next.Acquire(BootId, OtherLeaseId, 7000));
  assert(fixture.runtime.RecoverAfterInspection());
  assert(next.Acquire(BootId, OtherLeaseId, 7000));
  next.Disconnect(step);
  assert(!fixture.control.HasManualControl());
  assert(fixture.runtime.Generation() == 2);
}

void TestExpiredSessionCannotRevokeNextOwner() {
  Fixture fixture;
  LocalControlSession oldSession(fixture.runtime, "kufr-1", BootId, 0);
  assert(oldSession.Hello(1, "kufr-1", 0));
  assert(oldSession.Acquire(BootId, LeaseId, 0));
  SelectionStep step;
  fixture.runtime.Tick(6000, step);
  assert(!fixture.control.HasManualControl());
  assert(fixture.runtime.Generation() == 1);
  assert(fixture.runtime.RecoverAfterInspection());

  LocalControlSession nextSession(fixture.runtime, "kufr-1", BootId, 7000);
  assert(nextSession.Hello(1, "kufr-1", 7000));
  assert(nextSession.Acquire(BootId, OtherLeaseId, 7000));
  assert(!oldSession.Release(BootId, LeaseId, step));
  oldSession.Disconnect(step);
  assert(fixture.control.HasManualControl());
  assert(fixture.runtime.Generation() == 1);
  assert(nextSession.Heartbeat(BootId, OtherLeaseId, 7001));
  assert(nextSession.Release(BootId, OtherLeaseId, step));
  assert(fixture.runtime.Generation() == 2);
}

void TestConversationCancelAndStopShareGeneration() {
  Fixture fixture;
  LocalControlSession local(fixture.runtime, "kufr-1", BootId, 0);
  assert(local.Hello(1, "kufr-1", 0));
  assert(local.Acquire(BootId, LeaseId, 0));
  ConversationSession conversation(BootId, ConversationId);
  ConversationRuntimeSnapshot before;
  assert(fixture.state.Read(before));

  ConversationTextCommand command;
  command.bootId = BootId;
  command.conversationId = ConversationId;
  command.clientTurnId = TurnOneId;
  command.generation = before.generation;
  command.expectedRevision = before.deviceRevision;
  command.text = "Ahoj";
  assert(conversation.SubmitText(command, true, before.deviceRevision).error ==
         ConversationCommandError::None);
  assert(conversation.ValidateCancel(
             BootId, command.conversationId, before.generation, true) ==
         ConversationCommandError::None);
  assert(!fixture.runtime.CancelConversation(
      OtherLeaseId, before.generation, 100));
  assert(!fixture.runtime.CancelConversation(
      LeaseId, before.generation + 1, 100));
  assert(!fixture.runtime.CancelConversation(
      LeaseId, before.generation, 6000));
  assert(fixture.runtime.CancelConversation(
      LeaseId, before.generation, 100));
  assert(conversation.Cancel(
             BootId, command.conversationId, before.generation, true) ==
         ConversationCommandError::None);
  assert(conversation.Generation() == fixture.runtime.Generation());
  assert(!conversation.CompleteTurn(command.clientTurnId, before.generation));
  ConversationRuntimeSnapshot afterCancel;
  assert(fixture.state.Read(afterCancel));
  assert(afterCancel.generation == conversation.Generation());
  assert(afterCancel.version > before.version);
  assert(!fixture.state.Matches(before));

  command.clientTurnId = "turn-2";
  command.generation = afterCancel.generation;
  assert(conversation.SubmitText(command, true, afterCancel.deviceRevision).error ==
         ConversationCommandError::None);
  SelectionStep step;
  assert(fixture.runtime.Stop(step));
  assert(conversation.SynchronizeGeneration(fixture.runtime.Generation()));
  assert(!conversation.HasActiveTurn());
  assert(!conversation.CompleteTurn(command.clientTurnId,
                                    afterCancel.generation));
  assert(!conversation.SynchronizeGeneration(afterCancel.generation));
  assert(!fixture.state.Read(afterCancel));
}

void TestLeaseLossInvalidatesConversationTurn() {
  Fixture fixture;
  LocalControlSession local(fixture.runtime, "kufr-1", BootId, 0);
  assert(local.Hello(1, "kufr-1", 0));
  assert(local.Acquire(BootId, LeaseId, 0));
  ConversationSession conversation(BootId, ConversationId);
  ConversationRuntimeSnapshot snapshot;
  assert(fixture.state.Read(snapshot));
  ConversationTextCommand command;
  command.bootId = BootId;
  command.conversationId = ConversationId;
  command.clientTurnId = TurnOneId;
  command.generation = snapshot.generation;
  command.expectedRevision = snapshot.deviceRevision;
  command.text = "Ahoj";
  assert(conversation.SubmitText(command, true, snapshot.deviceRevision).error ==
         ConversationCommandError::None);

  SelectionStep step;
  assert(local.Release(BootId, LeaseId, step));
  assert(fixture.runtime.Generation() == snapshot.generation + 1);
  assert(conversation.SynchronizeGeneration(fixture.runtime.Generation()));
  assert(!conversation.HasActiveTurn());
  assert(!conversation.CompleteTurn(command.clientTurnId, snapshot.generation));
  assert(!fixture.state.Matches(snapshot));
  assert(conversation.SynchronizeGeneration(fixture.runtime.Generation()));
}

void TestGatewayAdmissionAndDuplicate() {
  Fixture fixture;
  LocalControlSession local(fixture.runtime, "kufr-1", BootId, 0);
  assert(local.Hello(1, "kufr-1", 0));
  assert(local.Acquire(BootId, LeaseId, 0));
  ConversationSession conversation(BootId, ConversationId);
  FakeConversationNetwork network;
  LocalConversationGateway gateway(
      local, fixture.runtime, fixture.state, conversation, network);
  ConversationRuntimeSnapshot snapshot;
  assert(fixture.state.Read(snapshot));
  ConversationTextCommand command;
  command.bootId = BootId;
  command.conversationId = ConversationId;
  command.clientTurnId = TurnOneId;
  command.generation = snapshot.generation;
  command.expectedRevision = snapshot.deviceRevision;
  command.text = "Ahoj";

  assert(gateway.SubmitText(command, OtherLeaseId, 100).error ==
         ConversationCommandError::Unauthorized);
  assert(gateway.SubmitText(command, LeaseId, 6000).error ==
         ConversationCommandError::Unauthorized);
  network.accept = false;
  assert(gateway.SubmitText(command, LeaseId, 100).error ==
         ConversationCommandError::Busy);
  assert(!conversation.HasActiveTurn());
  network.accept = true;
  assert(gateway.SubmitText(command, LeaseId, 100).error ==
         ConversationCommandError::None);
  assert(conversation.HasActiveTurn());
  assert(network.queued == 1 && network.attempts == 2);
  assert(network.lastText == command.text);
  assert(network.lastConversationId == command.conversationId);
  assert(network.lastTurnId == command.clientTurnId);
  assert(network.lastSnapshot.version == snapshot.version);
  assert(gateway.SubmitText(command, LeaseId, 100).duplicate);
  assert(network.queued == 1 && network.attempts == 2);
  assert(!gateway.CompleteTurn("other-turn", snapshot.generation));
  assert(gateway.CompleteTurn(command.clientTurnId, snapshot.generation));
  assert(gateway.SubmitText(command, LeaseId, 100).error ==
         ConversationCommandError::TurnAlreadySeen);
}

void TestGatewayCancelAndControlTransition() {
  Fixture fixture;
  LocalControlSession local(fixture.runtime, "kufr-1", BootId, 0);
  assert(local.Hello(1, "kufr-1", 0));
  assert(local.Acquire(BootId, LeaseId, 0));
  ConversationSession conversation(BootId, ConversationId);
  FakeConversationNetwork network;
  LocalConversationGateway gateway(
      local, fixture.runtime, fixture.state, conversation, network);
  ConversationRuntimeSnapshot snapshot;
  assert(fixture.state.Read(snapshot));
  ConversationTextCommand command;
  command.bootId = BootId;
  command.conversationId = ConversationId;
  command.clientTurnId = TurnOneId;
  command.generation = snapshot.generation;
  command.expectedRevision = snapshot.deviceRevision;
  command.text = "Ahoj";
  assert(gateway.SubmitText(command, LeaseId, 100).error ==
         ConversationCommandError::None);
  assert(gateway.Cancel(BootId, command.conversationId, OtherLeaseId,
                        snapshot.generation, 100) ==
         ConversationCommandError::Unauthorized);
  assert(gateway.Cancel(BootId, command.conversationId, LeaseId,
                        snapshot.generation, 6000) ==
         ConversationCommandError::Unauthorized);
  assert(gateway.Cancel(BootId, command.conversationId, LeaseId,
                        snapshot.generation, 100) ==
         ConversationCommandError::None);
  assert(network.cancels == 1);
  assert(!gateway.CompleteTurn(command.clientTurnId, snapshot.generation));
  ConversationRuntimeSnapshot afterCancel;
  assert(fixture.state.Read(afterCancel));
  assert(afterCancel.generation == conversation.Generation());
  ConversationSession observerConversation(BootId, OtherConversationId);
  LocalConversationGateway observerGateway(
      local, fixture.runtime, fixture.state, observerConversation, network);
  assert(observerGateway.OnControlTransition());
  assert(network.cancels == 1);

  command.clientTurnId = TurnTwoId;
  command.generation = afterCancel.generation;
  assert(gateway.SubmitText(command, LeaseId, 100).error ==
         ConversationCommandError::Busy);
  assert(!conversation.HasActiveTurn());
  network.cancelPending = false;
  assert(gateway.SubmitText(command, LeaseId, 100).error ==
         ConversationCommandError::None);
  SelectionStep step;
  assert(fixture.runtime.Stop(step));
  assert(gateway.OnControlTransition());
  assert(network.cancels == 2);
  assert(!conversation.HasActiveTurn());
  assert(!gateway.CompleteTurn(command.clientTurnId, afterCancel.generation));
  assert(gateway.OnControlTransition());
  assert(network.cancels == 2);
}

void TestGatewayCompletesAsyncFailureWithoutReusingTurnId() {
  Fixture fixture;
  LocalControlSession local(fixture.runtime, "kufr-1", BootId, 0);
  assert(local.Hello(1, "kufr-1", 0));
  assert(local.Acquire(BootId, LeaseId, 0));
  ConversationSession conversation(BootId, ConversationId);
  FakeConversationNetwork network;
  LocalConversationGateway gateway(
      local, fixture.runtime, fixture.state, conversation, network);
  ConversationRuntimeSnapshot snapshot;
  assert(fixture.state.Read(snapshot));
  ConversationTextCommand command;
  command.bootId = BootId;
  command.conversationId = ConversationId;
  command.clientTurnId = TurnOneId;
  command.generation = snapshot.generation;
  command.expectedRevision = snapshot.deviceRevision;
  command.text = "Ahoj";
  assert(gateway.SubmitText(command, LeaseId, 100).error ==
         ConversationCommandError::None);

  network.FailLastTurn();
  ConversationSession observerConversation(BootId, OtherConversationId);
  LocalConversationGateway observerGateway(
      local, fixture.runtime, fixture.state, observerConversation, network);
  assert(!observerGateway.PollNetworkFailure());
  assert(gateway.PollNetworkFailure());
  assert(!conversation.HasActiveTurn());
  assert(gateway.SubmitText(command, LeaseId, 100).error ==
         ConversationCommandError::TurnAlreadySeen);

  command.clientTurnId = TurnTwoId;
  assert(gateway.SubmitText(command, LeaseId, 100).error ==
         ConversationCommandError::None);
  network.failedTurns.Store(ConversationId, TurnOneId, snapshot.version);
  assert(!gateway.PollNetworkFailure());
  assert(conversation.HasActiveTurn());
  network.failedTurns.Store(
      ConversationId, TurnTwoId, snapshot.version + 1);
  assert(!gateway.PollNetworkFailure());
  assert(conversation.HasActiveTurn());
  network.FailLastTurn();
  assert(gateway.PollNetworkFailure());
  assert(!conversation.HasActiveTurn());
}

void TestGatewayClosesTurnOnRevisionOnlyChange() {
  Fixture fixture;
  LocalControlSession local(fixture.runtime, "kufr-1", BootId, 0);
  assert(local.Hello(1, "kufr-1", 0));
  assert(local.Acquire(BootId, LeaseId, 0));
  ConversationSession conversation(BootId, ConversationId);
  FakeConversationNetwork network;
  LocalConversationGateway gateway(
      local, fixture.runtime, fixture.state, conversation, network);
  ConversationRuntimeSnapshot snapshot;
  assert(fixture.state.Read(snapshot));
  ConversationTextCommand command;
  command.bootId = BootId;
  command.conversationId = ConversationId;
  command.clientTurnId = TurnOneId;
  command.generation = snapshot.generation;
  command.expectedRevision = snapshot.deviceRevision;
  command.text = "Ahoj";
  assert(gateway.SubmitText(command, LeaseId, 100).error ==
         ConversationCommandError::None);

  // A safe physical/inventory revision can invalidate a stored or playing
  // reply without advancing conversation generation.
  assert(fixture.state.Publish(snapshot.generation,
                               snapshot.deviceRevision + 1,
                               snapshot.readyMask,
                               snapshot.catalogVersion));
  assert(!gateway.CompleteTurn(TurnOneId, snapshot.generation));
  assert(!conversation.HasActiveTurn());
  assert(network.cancels == 1);
  assert(gateway.OnControlTransition());
  assert(network.cancels == 1);
}

void TestDelayedOldGatewayCannotCancelNewOwner() {
  Fixture fixture;
  LocalControlSession oldLocal(fixture.runtime, "kufr-1", BootId, 0);
  assert(oldLocal.Hello(1, "kufr-1", 0));
  assert(oldLocal.Acquire(BootId, LeaseId, 0));
  ConversationSession oldConversation(BootId, ConversationId);
  FakeConversationNetwork network;
  LocalConversationGateway oldGateway(
      oldLocal, fixture.runtime, fixture.state, oldConversation, network);
  ConversationRuntimeSnapshot oldSnapshot;
  assert(fixture.state.Read(oldSnapshot));
  ConversationTextCommand oldCommand;
  oldCommand.bootId = BootId;
  oldCommand.conversationId = ConversationId;
  oldCommand.clientTurnId = TurnOneId;
  oldCommand.generation = oldSnapshot.generation;
  oldCommand.expectedRevision = oldSnapshot.deviceRevision;
  oldCommand.text = "Ahoj";
  assert(oldGateway.SubmitText(oldCommand, LeaseId, 100).error ==
         ConversationCommandError::None);

  SelectionStep step;
  assert(fixture.runtime.Tick(6000, step));
  assert(fixture.runtime.RecoverAfterInspection());
  LocalControlSession newLocal(fixture.runtime, "kufr-1", BootId, 7000);
  assert(newLocal.Hello(1, "kufr-1", 7000));
  assert(newLocal.Acquire(BootId, OtherLeaseId, 7000));
  ConversationSession newConversation(BootId, OtherConversationId);
  LocalConversationGateway newGateway(
      newLocal, fixture.runtime, fixture.state, newConversation, network);
  ConversationRuntimeSnapshot newSnapshot;
  assert(fixture.state.Read(newSnapshot));
  ConversationTextCommand newCommand;
  newCommand.bootId = BootId;
  newCommand.conversationId = OtherConversationId;
  newCommand.clientTurnId = TurnTwoId;
  newCommand.generation = newSnapshot.generation;
  newCommand.expectedRevision = newSnapshot.deviceRevision;
  newCommand.text = "Zdravim";
  assert(newGateway.SubmitText(newCommand, OtherLeaseId, 7001).error ==
         ConversationCommandError::None);
  assert(oldGateway.OnControlTransition());
  assert(network.cancels == 0);
  assert(network.lastConversationId == OtherConversationId);
  assert(newConversation.HasActiveTurn());
  assert(newGateway.CompleteTurn(TurnTwoId, newSnapshot.generation));
}

void TestFailureInboxKeepsOtherConversationEvent() {
  ConversationTurnFailureInbox inbox;
  inbox.Store(ConversationId, TurnOneId, 7);
  inbox.Store(OtherConversationId, TurnTwoId, 8);
  assert(!inbox.TakeFor(OtherConversationId, TurnOneId, 7));
  assert(!inbox.TakeFor(ConversationId, TurnTwoId, 7));
  assert(!inbox.TakeFor(ConversationId, TurnOneId, 8));
  assert(inbox.TakeFor(ConversationId, TurnOneId, 7));
  assert(!inbox.TakeFor(ConversationId, TurnOneId, 7));
  assert(inbox.TakeFor(OtherConversationId, TurnTwoId, 8));
}

}  // namespace

int main() {
  TestObserverDisconnectDoesNotChangeControl();
  TestHelloAndLeaseGuards();
  TestExpiryAndOwnerDisconnect();
  TestExpiredSessionCannotRevokeNextOwner();
  TestConversationCancelAndStopShareGeneration();
  TestLeaseLossInvalidatesConversationTurn();
  TestGatewayAdmissionAndDuplicate();
  TestGatewayCancelAndControlTransition();
  TestGatewayCompletesAsyncFailureWithoutReusingTurnId();
  TestGatewayClosesTurnOnRevisionOnlyChange();
  TestDelayedOldGatewayCannotCancelNewOwner();
  TestFailureInboxKeepsOtherConversationEvent();
}

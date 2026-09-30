#include <cassert>
#include <algorithm>
#include <vector>

#include "../src/features/device_control/DeviceControlRuntime.h"
#include "../src/features/local_control/LocalControlOwner.h"
#include "../src/features/local_control/LocalControlTransport.h"
#include "../src/features/local_control/LocalConversationNetworkPort.h"

namespace {

constexpr char Boot[] = "c85743d5-1b72-41b8-8500-ae3c102b27d6";
constexpr char Lease[] = "a8287a95-b6a1-4501-bdaa-38721ec83191";
constexpr char Conversation[] = "ac665612-864c-4482-bb68-7415921119ae";
constexpr char Turn[] = "8475532e-92b2-487e-b476-7f98f0030e68";
constexpr char Command[] = "3d6104de-98c6-40b5-b10d-4f5675a63c51";

class Safety final : public SafetyPort {
 public:
  void DisableActuators() override { ++disabled; }
  void StartVibration(uint8_t) override { ++vibrations; }
  void ReleaseLatch(uint8_t) override {}
  int disabled = 0;
  int vibrations = 0;
};

class Journal final : public CommandJournal {
 public:
  bool HasPending() const override { return pending; }
  uint8_t PendingCompartment() const override { return pending ? 1 : 0; }
  uint8_t ReadyMask() const override { return 63; }
  bool Begin(const std::string&, uint8_t) override {
    pending = true;
    ++begins;
    return true;
  }
  bool Resolve(uint8_t) override {
    pending = false;
    return true;
  }
  bool PersistReadyMask(uint8_t) override { return true; }
  bool pending = false;
  int begins = 0;
};

class Network final : public LocalConversationNetworkPort {
 public:
  bool QueueTextTurn(const std::string&, const std::string&,
                     const std::string&,
                     const ConversationRuntimeSnapshot&) override {
    if (accept) {
      ++queued;
    }
    return accept;
  }
  bool QueueCancelFor(const std::string&, uint64_t) override {
    ++cancels;
    return true;
  }
  bool TakeFailedTurnFor(const std::string&, const std::string&,
                         uint64_t) override {
    const bool result = failed;
    failed = false;
    return result;
  }
  bool accept = true;
  bool failed = false;
  int queued = 0;
  int cancels = 0;
};

class Transport final : public LocalControlTransport {
 public:
  explicit Transport(LocalControlInbox& inbox) : inbox(inbox) {}
  bool Send(const LocalSocketAdmission::Handle&, const std::string&) override {
    return true;
  }
  void Close(const LocalSocketAdmission::Handle& handle) override {
    if (handle.socket >= 0) {
      ++closes;
      inbox.Disconnect(handle);
    }
  }
  LocalControlInbox& inbox;
  int closes = 0;
};

class Clock final : public LocalControlClockPort {
 public:
  uint64_t NowMs() const override { return now; }
  uint64_t now = 100;
};

class Output final : public LocalControlOutputPort {
 public:
  Output(LocalControlInbox& inbox, Clock& clock) : inbox(inbox), clock(clock) {}
  bool Welcome(const LocalSocketAdmission::Handle&, const std::string&,
                uint64_t) override { ++welcomes; return accept; }
  bool Snapshot(const LocalSocketAdmission::Handle&, const std::string& lease,
                 uint64_t) override {
    ++snapshots;
    lastLease = lease;
    return accept;
  }
  bool Heartbeat(const LocalSocketAdmission::Handle&,
                  const LocalControlMessage&, uint64_t) override {
    ++heartbeats;
    return accept;
  }
  bool CommandAck(const LocalSocketAdmission::Handle& handle,
                   const LocalControlMessage&, const CommandReceipt& receipt) override {
    lastCommand = receipt;
    if (delayAck) {
      clock.now = 7000;
    }
    if (injectStop) {
      LocalControlMessage stop;
      stop.kind = LocalControlMessageKind::Stop;
      stop.deviceId = "kufr-001";
      stop.bootId = Boot;
      assert(inbox.Submit(handle, stop));
    }
    return accept;
  }
  bool ConversationAck(const LocalSocketAdmission::Handle&,
                        const LocalControlMessage&,
                        const ConversationCommandReceipt& receipt) override {
    lastConversation = receipt;
    return accept;
  }
  LocalControlInbox& inbox;
  bool accept = true;
  bool injectStop = false;
  bool delayAck = false;
  Clock& clock;
  int welcomes = 0;
  int snapshots = 0;
  int heartbeats = 0;
  std::string lastLease;
  CommandReceipt lastCommand;
  ConversationCommandReceipt lastConversation;
};

class Ids final : public LocalControlIdPort {
 public:
  std::string NewLeaseId() override { return fail ? "" : Lease; }
  bool fail = false;
};

class Recovery final : public LocalControlRecoveryGate {
 public:
  bool InspectionAllowsRecovery() const override { return allow; }
  bool allow = false;
};

class Steps final : public LocalControlStepPort {
 public:
  void OnStep(const SelectionStep& step) override { effects.push_back(step.effect); }
  std::vector<SelectionEffect> effects;
};

struct Fixture {
  Fixture()
      : safety(port), sequence(safety),
        control("kufr-001", Boot, sequence, journal, 1), state(false, 0, ""),
        runtime(control, sequence, state, "neutral-1"),
        conversation(Boot, Conversation), inbox("kufr-001", Boot),
        transport(inbox), output(inbox, clock),
        owner("kufr-001", Boot, runtime, state, conversation, network,
              inbox, transport, output, ids, recovery, steps, clock) {
    safety.InitializeSafe();
    assert(runtime.RecoverAfterInspection());
  }
  LocalControlMessage Message(LocalControlMessageKind kind) const {
    LocalControlMessage message;
    message.kind = kind;
    message.messageId = Command;
    message.commandId = Command;
    message.deviceId = "kufr-001";
    message.bootId = Boot;
    message.controlLeaseId = Lease;
    message.conversationId = Conversation;
    message.clientTurnId = Turn;
    message.generation = runtime.Generation();
    message.expectedRevision = control.Revision();
    message.sequence = 1;
    message.issuedAtDeviceMs = 100;
    message.maxAgeMs = 1000;
    message.compartment = 1;
    message.text = "Visitor text";
    return message;
  }
  void Submit(LocalControlMessageKind kind, uint64_t nowMs = 100) {
    assert(inbox.Submit(handle, Message(kind)));
    Poll(nowMs);
  }
  void Poll(uint64_t nowMs) {
    clock.now = nowMs;
    owner.Poll();
  }
  void Connect(bool hello = true) {
    assert(inbox.Open(handle, 100));
    Poll(100);
    if (hello) {
      Submit(LocalControlMessageKind::Hello);
    }
  }
  Safety port;
  SafetyController safety;
  SelectionSequence sequence;
  Journal journal;
  DeviceControl control;
  ConversationRuntimeState state;
  DeviceControlRuntime runtime;
  ConversationSession conversation;
  Network network;
  LocalControlInbox inbox;
  Transport transport;
  Clock clock;
  Output output;
  Ids ids;
  Recovery recovery;
  Steps steps;
  LocalControlOwner owner;
  LocalSocketAdmission::Handle handle{7, 1};
};

void TestHelloAndObserver() {
  Fixture fixture;
  fixture.Connect(false);
  fixture.Poll(10101);
  assert(fixture.transport.closes == 1 && fixture.output.welcomes == 0);
  fixture.Poll(10102);
  fixture.handle = {7, 2};
  fixture.Connect();
  assert(fixture.output.welcomes == 1);
  const uint64_t revision = fixture.control.Revision();
  const uint64_t generation = fixture.runtime.Generation();
  SelectionStep step;
  assert(fixture.sequence.Start(1, 100, SelectionControlMode::Standalone, step));
  fixture.inbox.Disconnect({7, 1});
  fixture.Submit(LocalControlMessageKind::StateGet, 101);
  fixture.inbox.Disconnect(fixture.handle);
  fixture.Poll(102);
  assert(fixture.sequence.IsBusy());
  assert(fixture.control.Revision() == revision);
  assert(fixture.runtime.Generation() == generation);

  Fixture beforeHello;
  beforeHello.Connect(false);
  beforeHello.Submit(LocalControlMessageKind::StateGet);
  assert(beforeHello.transport.closes == 1);
  Fixture repeated;
  repeated.Connect();
  repeated.Submit(LocalControlMessageKind::Hello);
  assert(repeated.transport.closes == 1);
}

void TestLeaseAndText() {
  Fixture fixture;
  fixture.Connect();
  fixture.Submit(LocalControlMessageKind::Acquire);
  assert(fixture.control.HasManualControl() && fixture.output.lastLease == Lease);
  fixture.Submit(LocalControlMessageKind::Text);
  assert(fixture.network.queued == 1 && fixture.conversation.HasActiveTurn());
  fixture.Submit(LocalControlMessageKind::Text);
  assert(fixture.network.queued == 1 && fixture.output.lastConversation.duplicate);
  fixture.Submit(LocalControlMessageKind::Heartbeat, 200);
  assert(fixture.output.heartbeats == 1);
  assert(!fixture.owner.CompleteTurn(Command, fixture.runtime.Generation()));
  assert(fixture.owner.CompleteTurn(Turn, fixture.runtime.Generation()));
  assert(!fixture.conversation.HasActiveTurn());
  fixture.Submit(LocalControlMessageKind::Release, 201);
  assert(!fixture.control.HasManualControl());
  assert(fixture.runtime.Generation() == 1);
  Fixture failedOutput;
  failedOutput.Connect();
  failedOutput.Submit(LocalControlMessageKind::Acquire);
  failedOutput.output.accept = false;
  failedOutput.Submit(LocalControlMessageKind::Text);
  assert(failedOutput.network.queued == 1 && failedOutput.network.cancels == 1);
  assert(!failedOutput.control.HasManualControl());
  assert(!failedOutput.conversation.HasActiveTurn());
}

void TestExpiryCancelFailureAndReset() {
  Fixture expiry;
  expiry.Connect();
  expiry.Submit(LocalControlMessageKind::Acquire);
  expiry.Submit(LocalControlMessageKind::Text, 6100);
  assert(expiry.network.queued == 0 && !expiry.control.HasManualControl());
  assert(expiry.transport.closes == 1);
  Fixture cancel;
  cancel.Connect();
  cancel.Submit(LocalControlMessageKind::Acquire);
  cancel.Submit(LocalControlMessageKind::Text);
  cancel.Submit(LocalControlMessageKind::Cancel);
  assert(cancel.network.cancels == 1 && !cancel.conversation.HasActiveTurn());
  assert(cancel.runtime.Generation() == 1);
  Fixture networkFailure;
  networkFailure.Connect();
  networkFailure.Submit(LocalControlMessageKind::Acquire);
  networkFailure.Submit(LocalControlMessageKind::Text);
  networkFailure.network.failed = true;
  networkFailure.Poll(101);
  assert(!networkFailure.conversation.HasActiveTurn());
  Fixture blockedReset;
  blockedReset.Connect();
  blockedReset.Submit(LocalControlMessageKind::Acquire);
  blockedReset.Submit(LocalControlMessageKind::Stop);
  blockedReset.Submit(LocalControlMessageKind::Reset, 101);
  assert(blockedReset.control.NeedsRecovery() && blockedReset.transport.closes == 1);
  Fixture reset;
  reset.Connect();
  reset.Submit(LocalControlMessageKind::Acquire);
  reset.Submit(LocalControlMessageKind::Stop);
  reset.recovery.allow = true;
  reset.Submit(LocalControlMessageKind::Reset, 101);
  assert(!reset.control.NeedsRecovery() && reset.control.HasManualControl());
}

void TestStopBetweenAckAndBegin() {
  Fixture fixture;
  fixture.Connect();
  fixture.Submit(LocalControlMessageKind::Acquire);
  fixture.output.injectStop = true;
  fixture.Submit(LocalControlMessageKind::Selection);
  assert(fixture.output.lastCommand.error == CommandError::None);
  assert(fixture.journal.begins == 1 && fixture.journal.pending);
  assert(!fixture.sequence.IsBusy() && fixture.control.NeedsRecovery());
  assert(std::find(fixture.steps.effects.begin(), fixture.steps.effects.end(),
                   SelectionEffect::SearchStep) == fixture.steps.effects.end());
  assert(fixture.port.vibrations == 0);
  assert(!fixture.control.HasPendingSelection());
  Fixture delayed;
  delayed.Connect();
  delayed.Submit(LocalControlMessageKind::Acquire);
  delayed.output.delayAck = true;
  delayed.Submit(LocalControlMessageKind::Selection);
  assert(delayed.journal.begins == 1);
  assert(!delayed.control.HasManualControl());
  assert(!delayed.sequence.IsBusy() && delayed.control.NeedsRecovery());
  assert(std::find(delayed.steps.effects.begin(), delayed.steps.effects.end(),
                   SelectionEffect::SearchStep) == delayed.steps.effects.end());
  assert(std::find(delayed.steps.effects.begin(), delayed.steps.effects.end(),
                   SelectionEffect::Stopped) != delayed.steps.effects.end());
  Fixture full;
  full.Connect();
  full.Submit(LocalControlMessageKind::Acquire);
  for (size_t index = 0; index < LocalControlInbox::QueueCapacity; ++index) {
    assert(full.inbox.Submit(full.handle, full.Message(LocalControlMessageKind::Selection)));
  }
  assert(full.inbox.Submit(full.handle, full.Message(LocalControlMessageKind::Stop)));
  full.inbox.Disconnect(full.handle);
  full.Poll(101);
  assert(full.journal.begins == 0 && full.control.NeedsRecovery());
  assert(!full.control.HasManualControl());
  Fixture tick;
  tick.Connect();
  tick.Submit(LocalControlMessageKind::Acquire);
  tick.Submit(LocalControlMessageKind::Selection);
  assert(tick.sequence.IsBusy());
  assert(tick.inbox.Submit(tick.handle, tick.Message(LocalControlMessageKind::Stop)));
  tick.clock.now = 101;
  tick.owner.Tick();
  assert(!tick.sequence.IsBusy() && tick.port.vibrations == 0);
}

}  // namespace

int main() {
  TestHelloAndObserver();
  TestLeaseAndText();
  TestExpiryCancelFailureAndReset();
  TestStopBetweenAckAndBegin();
}

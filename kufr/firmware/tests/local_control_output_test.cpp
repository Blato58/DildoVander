#include <cassert>
#include <algorithm>
#include <array>
#include <iostream>
#include <vector>

#include "../src/features/device_control/PersistentCommandJournal.h"
#include "../src/features/local_control/LocalControlOutput.h"
#include "../src/features/local_control/LocalControlTransport.h"

namespace {

constexpr char Boot[] = "c85743d5-1b72-41b8-8500-ae3c102b27d6";
constexpr char Lease[] = "a8287a95-b6a1-4501-bdaa-38721ec83191";
constexpr char Conversation[] = "ac665612-864c-4482-bb68-7415921119ae";
constexpr char Command[] = "3d6104de-98c6-40b5-b10d-4f5675a63c51";
constexpr char Turn[] = "8475532e-92b2-487e-b476-7f98f0030e68";

class Storage final : public JournalStorage {
 public:
  Storage(bool populate, bool fail) : fail(fail) {
    if (populate) {
      PersistentCommandJournal initial(*this);
      assert(initial.PersistReadyMask(63));
    }
  }
  JournalReadResult Read(uint8_t* output, size_t capacity,
                         size_t& length) override {
    if (fail || data.size() > capacity) {
      return JournalReadResult::Failed;
    }
    if (data.empty()) {
      return JournalReadResult::Missing;
    }
    std::copy(data.begin(), data.end(), output);
    length = data.size();
    return JournalReadResult::Found;
  }
  bool Write(const uint8_t* input, size_t length) override {
    if (fail) {
      return false;
    }
    data.assign(input, input + length);
    return true;
  }
  bool fail;
  std::vector<uint8_t> data;
};

class Safety final : public SafetyPort {
 public:
  void DisableActuators() override {}
  void StartVibration(uint8_t) override {}
  void ReleaseLatch(uint8_t) override {}
};

class Transport final : public LocalControlTransport {
 public:
  bool Send(const LocalSocketAdmission::Handle&, const std::string& json) override {
    if (!accept) {
      return false;
    }
    messages.push_back(json);
    // The same shared strict/schema validator consumes these exact bytes.
    std::cout << json << '\n';
    return true;
  }
  void Close(const LocalSocketAdmission::Handle&) override {}
  bool accept = true;
  std::vector<std::string> messages;
};

struct Fixture {
  Fixture(uint64_t calibration = 1, bool populated = true, bool fault = false)
      : storage(populated, fault), journal(storage), safety(port), sequence(safety),
        control("kufr-001", Boot, sequence, journal, calibration),
        conversation(Boot, Conversation),
        output("kufr-001", Boot, control, journal, safety, sequence,
               conversation, transport) {
    safety.InitializeSafe();
  }
  bool Snapshot(const std::string& lease = "", uint64_t nowMs = 100) {
    return output.Snapshot({7, 1}, lease, nowMs);
  }
  void Ready() {
    assert(control.RecoverAfterInspection());
  }
  void Acquire() {
    Ready();
    assert(control.AcquireManualControl(Lease, 100));
  }
  SelectionCommand Select(bool begin = true) {
    SelectionCommand command;
    command.commandId = Command;
    command.deviceId = "kufr-001";
    command.bootId = Boot;
    command.controlLeaseId = Lease;
    command.sequence = 1;
    command.expectedRevision = control.Revision();
    command.issuedAtDeviceMs = 100;
    command.maxAgeMs = 1000;
    command.compartment = 1;
    assert(control.AcceptSelection(command, true, 100).error == CommandError::None);
    SelectionStep step;
    if (begin) {
      assert(control.BeginAcceptedSelection(100, step));
    }
    return command;
  }
  Storage storage;
  PersistentCommandJournal journal;
  Safety port;
  SafetyController safety;
  SelectionSequence sequence;
  DeviceControl control;
  ConversationSession conversation;
  Transport transport;
  LocalControlOutput output;
};

void TestCurrentStateAndBounds() {
  Fixture uncalibrated(0, false);
  assert(uncalibrated.output.Welcome({7, 1}, Command, 10));
  assert(uncalibrated.transport.messages.back().find("\"calibrationRevision\":0") !=
         std::string::npos);
  assert(!uncalibrated.Snapshot("", 9007199254740992ULL));
  assert(!uncalibrated.output.Welcome({7, 1}, "invalid", 10));
  uncalibrated.transport.accept = false;
  assert(!uncalibrated.Snapshot());
  Fixture fault(1, false, true);
  assert(fault.Snapshot());
  assert(fault.transport.messages.back().find("\"faults\":[\"journal_failed\"]") !=
         std::string::npos);
  Fixture autonomous;
  autonomous.Ready();
  assert(autonomous.control.BeginAutonomousPresentation(Command, 2,
                                                       autonomous.control.Revision()));
  assert(autonomous.Snapshot());
  assert(autonomous.transport.messages.back().find("\"phase\":\"presenting\"") !=
         std::string::npos);
  assert(autonomous.control.CompleteAutonomousPresentation(2));
  assert(autonomous.Snapshot());
}

void TestAcceptanceAndIdleStop() {
  Fixture accepted;
  accepted.Acquire();
  accepted.Select(false);
  assert(accepted.Snapshot(Lease));
  assert(accepted.transport.messages.back().find("\"phase\":\"ready\"") !=
         std::string::npos);
  assert(accepted.transport.messages.back().find(Command) != std::string::npos);

  Fixture idle;
  idle.Ready();
  const uint64_t revision = idle.control.Revision();
  SelectionStep step;
  assert(idle.control.Stop(step));
  assert(idle.control.Revision() == revision + 1);
  assert(!idle.control.Stop(step));
  assert(idle.control.Revision() == revision + 1);
  assert(idle.Snapshot());
  assert(idle.transport.messages.back().find("\"phase\":\"stopped\"") !=
         std::string::npos);

  Fixture safetyFault;
  safetyFault.Ready();
  assert(safetyFault.safety.ReportFault());
  assert(safetyFault.Snapshot());
  assert(safetyFault.transport.messages.back().find("\"phase\":\"fault\"") !=
         std::string::npos);
}

void TestCommandAndConversationAcks() {
  Fixture fixture;
  fixture.Acquire();
  assert(fixture.Snapshot(Lease));
  LocalControlMessage request;
  request.kind = LocalControlMessageKind::Text;
  request.messageId = Command;
  request.commandId = Command;
  request.controlLeaseId = Lease;
  request.clientTurnId = Turn;
  assert(fixture.output.Heartbeat({7, 1}, request, 101));
  assert(!fixture.output.Heartbeat({7, 1}, request, 6100));
  for (unsigned index = 0; index <= static_cast<unsigned>(CommandError::JournalFailed); ++index) {
    const auto error = static_cast<CommandError>(index);
    assert(fixture.output.CommandAck({7, 1}, request, {error}));
  }
  for (unsigned index = 0;
       index <= static_cast<unsigned>(ConversationCommandError::TurnLimitReached); ++index) {
    const auto error = static_cast<ConversationCommandError>(index);
    assert(fixture.output.ConversationAck({7, 1}, request, {error, index == 0}));
  }
  request.kind = LocalControlMessageKind::Cancel;
  request.clientTurnId.clear();
  assert(fixture.output.ConversationAck({7, 1}, request, {}));
  ConversationTextCommand text;
  text.bootId = Boot;
  text.conversationId = Conversation;
  text.clientTurnId = Turn;
  text.expectedRevision = fixture.control.Revision();
  text.text = "Private visitor text";
  assert(fixture.conversation.SubmitText(text, true, fixture.control.Revision()).error ==
         ConversationCommandError::None);
  assert(fixture.Snapshot(Lease));
  assert(fixture.transport.messages.back().find("Private visitor text") == std::string::npos);
  assert(fixture.transport.messages.back().find(Turn) != std::string::npos);
}

void TestTerminalResultsAndPhases() {
  Fixture fixture;
  fixture.Acquire();
  fixture.Select();
  SelectionStep step;
  SelectionPhase previous = SelectionPhase::Idle;
  for (uint64_t nowMs = 100; nowMs < 4000; nowMs += 10) {
    fixture.control.Tick(nowMs, step);
    const auto phase = fixture.sequence.CurrentPhase();
    if (phase != previous) {
      assert(fixture.Snapshot(Lease, nowMs));
      previous = phase;
    }
    if (!fixture.control.HasPendingSelection()) {
      break;
    }
  }
  assert(fixture.control.LastResult().outcome == CommandOutcome::Completed);
  assert(fixture.control.LastResult().revision == fixture.control.Revision());
  const size_t before = fixture.transport.messages.size();
  assert(fixture.Snapshot(Lease, 3000));
  assert(fixture.transport.messages.size() == before + 1);  // No repeated terminal event.
  const auto& snapshot = fixture.transport.messages.back();
  assert(snapshot.find("\"status\":\"completed\"") != std::string::npos);
  assert(snapshot.find("\"physicalVerification\":\"unavailable\"") != std::string::npos);
  fixture.control.DisconnectManualControl(step);
  assert(fixture.output.Welcome({7, 2}, Command, 3001));

  Fixture stopped;
  stopped.Acquire();
  stopped.Select();
  stopped.control.Stop(step);
  assert(stopped.Snapshot(Lease));
  assert(stopped.control.LastResult().outcome == CommandOutcome::OutcomeUnknown);
  assert(stopped.transport.messages.back().find("outcome_unknown") != std::string::npos);
}

}  // namespace

int main() {
  TestCurrentStateAndBounds();
  TestAcceptanceAndIdleStop();
  TestCommandAndConversationAcks();
  TestTerminalResultsAndPhases();
}

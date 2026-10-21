#include <cassert>

#include <fstream>
#include <iterator>
#include <string>

#include "../src/features/local_control/LocalControlCodec.h"
#include "../src/features/conversation/ConversationStartCodec.h"
#include "../src/features/voice_turn/VoiceTestContext.h"

namespace {

constexpr char Id[] = "a8287a95-b6a1-4501-bdaa-38721ec83191";

std::string Common(const std::string& type) {
  return "\"protocolVersion\":1,\"type\":\"" + type +
         "\",\"messageId\":\"" + Id + "\",\"deviceId\":\"kufr-001\"";
}

std::string Boot() { return ",\"bootId\":\"" + std::string(Id) + "\""; }
std::string Lease() { return ",\"controlLeaseId\":\"" + std::string(Id) + "\""; }

std::string Text(const std::string& text) {
  return "{" + Common("conversation.text") + Boot() + Lease() +
         ",\"conversationId\":\"" + Id + "\",\"clientTurnId\":\"" + Id +
         "\",\"generation\":4,\"expectedRevision\":9,\"text\":\"" + text + "\"}";
}

bool Parse(const std::string& value, LocalControlMessage& message) {
  return ParseLocalControlMessage(value.data(), value.size(), message);
}

void TestConnectionAndLease() {
  LocalControlMessage message;
  assert(Parse("{" + Common("hello") + "}", message));
  assert(message.kind == LocalControlMessageKind::Hello);
  assert(message.messageId == Id && message.deviceId == "kufr-001");
  assert(Parse("{" + Common("state.get") + Boot() + "}", message));
  assert(Parse("{" + Common("control.acquire") + Boot() + "}", message));
  assert(Parse("{" + Common("heartbeat") + Boot() + Lease() + "}", message));
  assert(Parse("{" + Common("control.release") + Boot() + Lease() + "}", message));
  assert(Parse("{" + Common("safety.stop") + Boot() + "}", message));
  assert(Parse("{" + Common("safety.reset") + Boot() + Lease() +
               ",\"inspectionConfirmed\":true}", message));
  assert(!Parse("{" + Common("safety.reset") + Boot() + Lease() +
                ",\"inspectionConfirmed\":false}", message));
  assert(!Parse("{" + Common("heartbeat") + Boot() + "}", message));
  assert(!Parse("{" + Common("hello") + Boot() + "}", message));
  assert(!Parse("{" + Common("state.get") + Boot() + Lease() + "}", message));
  for (const char* type : {"control.acquire", "control.release", "safety.reset"}) {
    const std::string fields = Boot() + (std::string(type) == "control.acquire" ? "" : Lease()) +
        (std::string(type) == "safety.reset" ? ",\"inspectionConfirmed\":true" : "");
    assert(Parse("{" + Common(type) + fields + ",\"acknowledge\":true}", message));
    assert(message.acknowledge);
    assert(!Parse("{" + Common(type) + fields + ",\"acknowledge\":false}", message));
    assert(!Parse("{" + Common(type) + fields + ",\"acknowledge\":true,\"acknowledge\":true}", message));
  }
  assert(!Parse("{" + Common("hello") + ",\"acknowledge\":true}", message));
  assert(!Parse("{" + Common("state.get") + Boot() + ",\"acknowledge\":true}", message));
  assert(!Parse("{" + Common("heartbeat") + Boot() + Lease() + ",\"acknowledge\":true}", message));
}

void TestOperationEpoch() {
  LocalControlMessage message;
  const std::string state = "{" + Common("state.get") + Boot();
  assert(Parse(state + ",\"includeOperationEpoch\":true}", message));
  assert(message.includeOperationEpoch);
  assert(!Parse(state + ",\"includeOperationEpoch\":false}", message));
  assert(!Parse("{" + Common("hello") + ",\"includeOperationEpoch\":true}", message));
  const std::string ack = "{" + Common("offline_sync_ack") + Boot() +
      ",\"commandId\":\"" + Id + "\"";
  for (const char* valid : {"0", "1", "9007199254740993", "18446744073709551615"}) {
    assert(Parse(ack + ",\"operationEpoch\":\"" + valid + "\"}", message));
    assert(message.hasOperationEpoch);
  }
  assert(message.operationEpoch == UINT64_MAX);
  for (const char* invalid : {"", "00", "01", "-1", "+1", "1e0", "1.0", "18446744073709551616", "99999999999999999999"}) {
    assert(!Parse(ack + ",\"operationEpoch\":\"" + invalid + "\"}", message));
  }
  assert(!Parse(ack + ",\"operationEpoch\":1}", message));
  assert(!Parse(state + ",\"operationEpoch\":\"1\"}", message));
  assert(Parse(ack + "}", message) && !message.hasOperationEpoch && message.operationEpoch == 0);
}

void TestOfflineEnvelope() {
  LocalControlMessage message;
  for (const char* type : {"offline_preflight", "offline_outbox"}) {
    assert(Parse("{" + Common(type) + Boot() + "}", message));
    assert(!Parse("{" + Common(type) + Boot() + ",\"generation\":1}", message));
  }
  for (const char* type : {"quiet", "offline_ambient"}) {
    assert(Parse("{" + Common(type) + Boot() + ",\"enabled\":true}", message));
    assert(message.enabled);
    assert(Parse("{" + Common(type) + Boot() + ",\"enabled\":false}", message));
    assert(!message.enabled);
    assert(!Parse("{" + Common(type) + Boot() + "}", message));
  }
  assert(Parse("{" + Common("offline_arm") + Boot() + Lease() +
      ",\"generation\":4,\"expectedRevision\":9}", message));
  assert(!Parse("{" + Common("offline_arm") + Boot() +
      ",\"generation\":4,\"expectedRevision\":9}", message));
  const std::string start = "{" + Common("offline_start") + Boot() +
      ",\"generation\":4,\"expectedRevision\":9,\"compartment\":6,\"shortVariant\":true}";
  assert(Parse(start, message));
  assert(message.kind == LocalControlMessageKind::OfflineStart && message.shortVariant && message.compartment == 6);
  assert(!Parse(start.substr(0, start.size() - 1) + ",\"shortVariant\":false}", message));
  assert(Parse("{" + Common("offline_sync_ack") + Boot() + ",\"commandId\":\"" + Id + "\"}", message));
}

void TestTextAndHostileJson() {
  LocalControlMessage message;
  assert(Parse(Text("Ahoj, \\u010desky \\ud83e\\ude84"), message));
  assert(message.kind == LocalControlMessageKind::Text);
  assert(message.text == u8"Ahoj, \u010desky \U0001fa84");
  assert(message.generation == 4 && message.expectedRevision == 9);
  assert(Parse(Text(std::string(1000, 'x')), message));
  assert(!Parse(Text(std::string(1001, 'x')), message));
  assert(Parse(Text(std::string(950, 'x')) + std::string(3000, ' '), message));
  assert(!Parse(Text(std::string(950, 'x')) + std::string(8000, ' '), message));
  assert(!Parse(Text("\\ud800"), message));
  assert(!Parse(Text(std::string(1, static_cast<char>(0xff))), message));
  assert(!Parse(Text(""), message));
  assert(!Parse("{" + Common("hello") + ",\"type\":\"hello\"}", message));
  assert(!Parse("{" + Common("hello") + ",\"\\u0074ype\":\"hello\"}", message));
  assert(!Parse("{" + Common("hello") + ",\"servo\":4}", message));
  assert(!Parse("{" + Common("hello") + ",\"payload\":{\"compartment\":1}}", message));
  assert(!Parse("{" + Common("hello") + "}null", message));
  for (const char* number : {"1.0", "1e0", "01", "-1", "\"1\"", "null"}) {
    std::string value = "{" + Common("hello") + "}";
    value.replace(value.find(":1,") + 1, 1, number);
    assert(!Parse(value, message));
  }
  std::string unsafe = Text("x");
  unsafe.replace(unsafe.find("\"generation\":4"), 14,
                 "\"generation\":9007199254740992");
  assert(!Parse(unsafe, message));
  assert(!Parse(std::string(2049, ' ') + "{" + Common("hello") + "}", message));
  assert(!Parse("{}", message));
  assert(!Parse("[]", message));
  assert(!Parse("{" + Common("heartbeat.ack") + "}", message));
  assert(message.messageId.empty() && message.text.empty());
}

void TestPhysicalEnvelope() {
  const std::string fields =
      "\"protocolVersion\":1,\"commandId\":\"" + std::string(Id) +
      "\",\"deviceId\":\"kufr-001\"" + Boot() + Lease() +
      ",\"sequence\":1,\"expectedRevision\":0,\"issuedAtDeviceMs\":50,\"maxAgeMs\":2000";
  LocalControlMessage message;
  const std::string selection = "{" + fields +
      ",\"type\":\"selection.start\",\"payload\":{\"compartment\":6,\"selectionSource\":\"manual\"}}";
  assert(Parse(selection, message));
  assert(message.compartment == 6 && message.maxAgeMs == 2000);
  assert(message.kind == LocalControlMessageKind::Selection);
  assert(Parse("{" + fields +
      ",\"type\":\"inventory.confirmReload\",\"payload\":{\"inspectionConfirmed\":true,\"compartment\":1}}", message));
  std::string invalid = selection;
  invalid.replace(invalid.find("\"compartment\":6"), 15, "\"compartment\":7");
  assert(!Parse(invalid, message));
  invalid = selection;
  invalid.insert(invalid.find("\"selectionSource\""), "\"compartment\":6,");
  assert(!Parse(invalid, message));
}

}  // namespace

int main(int argc, char** argv) {
  LocalControlMessage dance;
  const std::string common = "\"protocolVersion\":1,\"messageId\":\"" + std::string(Id) +
      "\",\"deviceId\":\"kufr-001\",\"bootId\":\"" + Id + "\"";
  assert(Parse("{" + common + ",\"type\":\"dance.start\",\"expectedRevision\":1}", dance));
  assert(dance.kind == LocalControlMessageKind::DanceStart && dance.expectedRevision == 1);
  assert(!Parse("{" + common + ",\"type\":\"dance.start\"}", dance));
  assert(Parse("{" + common + ",\"type\":\"dance.stop\"}", dance));
  assert(dance.kind == LocalControlMessageKind::DanceStop);
  assert(!Parse("{" + common + ",\"type\":\"dance.stop\",\"expectedRevision\":1}", dance));
  TestConnectionAndLease();
  {
    LocalControlMessage context;
    const std::string ticket(43, 'A'), id(64, 'a');
    assert(MatchesActiveVoiceTestContext(id, id, true, false));
    assert(MatchesActiveVoiceTestContext(id, id, false, true));
    assert(!MatchesActiveVoiceTestContext(id, id, false, false)); // old failed/completed status
    assert(!MatchesActiveVoiceTestContext(id, std::string(64, 'b'), true, true));
    assert(!MatchesActiveVoiceTestContext("", id, true, true));
    const auto start = "{" + Common("voice.start") + Boot() + ",\"expectedRevision\":1";
    assert(Parse(start + ",\"testContext\":\"" + ticket + "\"}", context));
    assert(context.testContext == ticket);
    assert(!Parse(start + ",\"testContext\":\"" + ticket + "!\"}", context));
    assert(!Parse(start + ",\"testContextId\":\"" + id + "\"}", context));
    const auto stop = "{" + Common("voice.stop") + Boot();
    assert(Parse(stop + ",\"testContextId\":\"" + id + "\"}", context));
    assert(context.testContextId == id);
    assert(!Parse(stop + ",\"testContextId\":\"" + std::string(64, 'A') + "\"}", context));
    assert(!Parse(stop + ",\"testContext\":\"" + ticket + "\"}", context));
    ConversationStartRequest request;
    request.deviceId = "kufr-001"; request.bootId = Id; request.idempotencyKey = Id;
    request.catalogVersion = "catalog"; request.audioConsent = true; request.operationEpoch = UINT64_MAX;
    request.testContext = ticket;
    std::string original, retry;
    assert(BuildConversationStartRequest(request, original));
    assert(BuildConversationStartRequest(request, retry) && original == retry);
    assert(original.find("\"testContext\":\"" + ticket + "\"") != std::string::npos);
    assert(original.find("18446744073709551615") != std::string::npos);
    request.imageConsent = true;
    assert(!BuildConversationStartRequest(request, retry));
    request.imageConsent = false; request.audioConsent = false;
    assert(!BuildConversationStartRequest(request, retry));
  }
  TestOfflineEnvelope();
  TestOperationEpoch();
  TestTextAndHostileJson();
  TestPhysicalEnvelope();
  // Shared repository fixtures are supplied by the local/CI runner.
  bool expectValid = true;
  for (int index = 1; index < argc; ++index) {
    if (std::string(argv[index]) == "--reject") {
      expectValid = false;
      continue;
    }
    std::ifstream input(argv[index], std::ios::binary);
    assert(input.is_open());
    const std::string value((std::istreambuf_iterator<char>(input)),
                             std::istreambuf_iterator<char>());
    LocalControlMessage message;
    assert(Parse(value, message) == expectValid);
  }
}

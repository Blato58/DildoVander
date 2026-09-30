#include <cassert>

#include <fstream>
#include <iterator>
#include <string>

#include "../src/features/local_control/LocalControlCodec.h"

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
  TestConnectionAndLease();
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

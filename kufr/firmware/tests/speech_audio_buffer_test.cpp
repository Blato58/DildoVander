#include <algorithm>
#include <cassert>
#include <cstring>
#include <string>
#include <vector>

#include "../src/features/conversation/SpeechAudioBuffer.h"

namespace {
constexpr char Id[] = "8475532e-92b2-487e-b476-7f98f0030e68";
constexpr char AudioId[] = "ad64d2ac-d76a-4e5d-9ce7-a0b468f31c85";

std::string Operation(const std::string& status = "completed",
                      const std::string& target = "device") {
  return std::string("{\"operationId\":\"") + Id +
      "\",\"kind\":\"speech\",\"status\":\"" + status +
      "\",\"correlation\":{\"deviceId\":\"device-a\",\"bootId\":\"" + Id +
      "\",\"conversationId\":\"" + Id + "\",\"clientTurnId\":\"" + Id +
      "\",\"generation\":0,\"deviceRevision\":0},\"result\":" +
      (status == "completed"
          ? std::string("{\"audioId\":\"") + AudioId +
              "\",\"expiresAt\":\"2026-10-01T12:02:00+00:00\",\"outputTarget\":\"" +
              target + "\"}"
          : "null") + ",\"error\":null}";
}

std::vector<uint8_t> Wav() {
  std::vector<uint8_t> bytes(108, 0);
  std::memcpy(bytes.data(), "RIFF", 4);
  bytes[4] = 100;
  std::memcpy(bytes.data() + 8, "WAVEfmt ", 8);
  bytes[16] = 16;
  bytes[20] = 1;
  bytes[22] = 1;
  bytes[24] = 0;
  bytes[25] = 125; // 32000 Hz
  bytes[28] = 0;
  bytes[29] = 250; // 64000 B/s
  bytes[32] = 2;
  bytes[34] = 16;
  std::memcpy(bytes.data() + 36, "data", 4);
  bytes[40] = 64;
  std::fill(bytes.begin() + 44, bytes.end(), uint8_t(37));
  return bytes;
}

void CheckCodec() {
  std::string body;
  assert(BuildSpeechRequest(Id, body));
  assert(body == std::string("{\"turnId\":\"") + Id + "\",\"outputTarget\":\"device\"}");
  assert(!BuildSpeechRequest("00000000-0000-0000-0000-000000000000", body));
  assert(body.empty());
  SpeechOperationObservation observation;
  const std::string valid = Operation();
  assert(ParseSpeechOperation(valid.data(), valid.size(), observation));
  uint64_t now = 0;
  assert(ParseConversationUtcExpiry("2026-10-01T12:00:00Z", now));
  const TurnCorrelation expected = observation.correlation;
  assert(SpeechRemainingMs(Id, expected, now, observation) == 119000);
  assert(SpeechRemainingMs(Id, expected, now + 119, observation) == 0);
  assert(SpeechRemainingMs(Id, expected, now - 1, observation) == 0);
  assert(SpeechRemainingMs(AudioId, expected, now, observation) == 0);
  auto changed = expected;
  changed.deviceRevision++;
  assert(SpeechRemainingMs(Id, changed, now, observation) == 0);
  changed = expected;
  changed.generation++;
  assert(SpeechRemainingMs(Id, changed, now, observation) == 0);
  changed = expected;
  changed.deviceId = "other-device";
  assert(SpeechRemainingMs(Id, changed, now, observation) == 0);

  for (const char* status : {"pending", "running", "cancelled", "unknown"}) {
    const auto value = Operation(status);
    assert(ParseSpeechOperation(value.data(), value.size(), observation));
    assert(SpeechRemainingMs(Id, expected, now, observation) == 0);
  }
  auto phone = Operation("completed", "phone");
  assert(!ParseSpeechOperation(phone.data(), phone.size(), observation));
  for (const char* fragment : {"\"kind\":\"turn\",", "\"extra\":1,", "\"status\":\"completed\","}) {
    const std::string duplicate = std::string("{") + fragment + valid.substr(1);
    assert(!ParseSpeechOperation(duplicate.data(), duplicate.size(), observation));
  }
  assert(!ParseSpeechOperation(valid.data(), valid.size() - 1, observation));
  assert(!ParseSpeechOperation(nullptr, 0, observation));
}

void CheckBuffer() {
  ConversationRuntimeState runtime(true, 63, "catalog-1");
  ConversationRuntimeSnapshot snapshot;
  assert(runtime.Read(snapshot));
  auto wav = Wav();
  std::vector<uint8_t> storage(128, 0);
  SpeechAudioBuffer buffer(storage.data(), storage.size());
  SpeechOperationObservation result;
  const auto json = Operation();
  assert(ParseSpeechOperation(json.data(), json.size(), result));
  uint64_t utc = 0;
  assert(ParseConversationUtcExpiry("2026-10-01T12:00:00Z", utc));
  const auto begin = [&](size_t length = 108, const std::string& type = "audio/wav") {
    return buffer.Begin(Id, result.correlation, result, snapshot, runtime,
                        utc, 1000, length, type, 32000, 1);
  };
  assert(!begin(130));
  assert(!begin(108, "audio/mpeg"));
  assert(!begin(107));
  assert(begin());
  uint8_t output[8] = {};
  assert(buffer.Append(wav.data(), 43, 1001, runtime));
  assert(buffer.ReadPcm(0, output, sizeof(output), 1002, runtime) == 0);
  assert(!buffer.Finish(1002, runtime));
  assert(std::all_of(storage.begin(), storage.end(), [](uint8_t byte) { return byte == 0; }));
  assert(begin());
  assert(buffer.Append(wav.data(), wav.size(), 1001, runtime));
  assert(buffer.Finish(1002, runtime));
  assert(buffer.ReadPcm(0, output, sizeof(output), 1003, runtime) == 8);
  assert(std::all_of(output, output + 8, [](uint8_t byte) { return byte == 37; }));
  assert(buffer.ReadPcm(1, output, sizeof(output), 1003, runtime) == 0);
  assert(buffer.ReadPcm(62, output, sizeof(output), 1003, runtime) == 2);
  assert(runtime.Publish(1, 1, 63, "catalog-1"));
  assert(buffer.ReadPcm(0, output, sizeof(output), 1004, runtime) == 0);
  assert(std::all_of(storage.begin(), storage.end(), [](uint8_t byte) { return byte == 0; }));
  assert(!begin());
  assert(runtime.Read(snapshot));
  result.correlation.generation = snapshot.generation;
  result.correlation.deviceRevision = snapshot.deviceRevision;
  for (size_t offset : {size_t(0), size_t(4), size_t(8), size_t(16), size_t(20),
                        size_t(22), size_t(24), size_t(28), size_t(32), size_t(34), size_t(36), size_t(40)}) {
    auto broken = wav;
    broken[offset]++;
    assert(begin());
    assert(buffer.Append(broken.data(), broken.size(), 1001, runtime));
    assert(!buffer.Finish(1002, runtime));
    assert(std::all_of(storage.begin(), storage.end(), [](uint8_t byte) { return byte == 0; }));
  }
  assert(begin());
  assert(!buffer.Append(wav.data(), wav.size() + 1, 1001, runtime));
  assert(begin());
  assert(buffer.Append(wav.data(), wav.size(), 1001, runtime));
  assert(!buffer.Finish(120000, runtime));
  assert(begin());
  assert(buffer.Append(wav.data(), wav.size(), 1001, runtime));
  buffer.Clear();
  assert(!buffer.Finish(1002, runtime));
  assert(std::all_of(storage.begin(), storage.end(), [](uint8_t byte) { return byte == 0; }));
}
}  // namespace

int main() {
  CheckCodec();
  CheckBuffer();
}

#include <algorithm>
#include <cassert>
#include <cstring>
#include <vector>

#include "../src/features/conversation/SpeechOutputCoordinator.h"

namespace {
constexpr char Id[] = "8475532e-92b2-487e-b476-7f98f0030e68";
constexpr char SpeechId[] = "ad64d2ac-d76a-4e5d-9ce7-a0b468f31c85";
class Clock final : public SpeechClockPort {
 public:
  uint64_t now = 1000;
  uint64_t utc = 0;
  Clock() { assert(ParseConversationUtcExpiry("2026-10-01T12:00:00Z", utc)); }
  uint64_t NowMs() override { return now; }
  uint64_t UtcSeconds() override { return utc + (now - 1000) / 1000; }
  void WaitMs(uint32_t milliseconds) override { now += milliseconds; }
};
class Transport final : public SpeechTransportPort {
 public:
  unsigned submits = 0;
  unsigned polls = 0;
  unsigned downloads = 0;
  unsigned releases = 0;
  bool lostAcceptance = false;
  bool staleResult = false;
  bool foreverPending = false;
  bool failDownload = false;
  bool stopInDownload = false;
  Clock& clock;
  ConversationRuntimeState& runtime;
  Transport(Clock& time, ConversationRuntimeState& state) : clock(time), runtime(state) {}
  int Submit(const std::string& conversation, const std::string& turn,
             const std::string& key, const char*, std::string& operation) override {
    assert(conversation == Id && turn == Id && key == Id);
    ++submits;
    if (lostAcceptance && submits == 1) { return 0; }
    operation = SpeechId;
    return 202;
  }
  int Poll(const std::string& operation, const char*, SpeechOperationObservation& result) override {
    ++polls;
    assert(operation == SpeechId);
    result.operationId = SpeechId;
    result.correlation = {"device-a", Id, Id, Id, 0, 0};
    if (staleResult) { result.correlation.generation = 1; }
    result.status = polls == 1 || foreverPending ? TurnOperationStatus::Running : TurnOperationStatus::Completed;
    result.audioId = SpeechId;
    result.outputTarget = "device";
    result.expiresAt = "2026-10-01T12:02:00Z";
    return 200;
  }
  int Release(const std::string& operation, const std::string& key, const char*) override {
    assert(operation == SpeechId && key == Id);
    ++releases;
    return 204;
  }
  int Download(const std::string& operation, const TurnCorrelation& expected,
               const SpeechOperationObservation& result,
               const ConversationRuntimeSnapshot& snapshot,
               const ConversationRuntimeState& state, uint64_t utc,
               const char*, SpeechAudioBuffer& buffer) override {
    ++downloads;
    if (failDownload) { return 410; }
    std::vector<uint8_t> bytes(46, 0);
    std::memcpy(bytes.data(), "RIFF", 4);
    bytes[4] = 38;
    std::memcpy(bytes.data() + 8, "WAVEfmt ", 8);
    bytes[16] = bytes[34] = 16;
    bytes[20] = bytes[22] = 1;
    bytes[25] = 125;
    bytes[29] = 250;
    bytes[32] = bytes[40] = 2;
    std::memcpy(bytes.data() + 36, "data", 4);
    bytes[44] = bytes[45] = 37;
    assert(buffer.Begin(operation, expected, result, snapshot, state, utc,
                        clock.now, bytes.size(), "audio/wav", 32000, 1));
    assert(buffer.Append(bytes.data(), bytes.size(), clock.now, state));
    if (stopInDownload) { runtime.Invalidate(); return 0; }
    assert(buffer.Finish(clock.now, state));
    return 200;
  }
};

void CheckScenario(bool consent, bool invalidSource, bool lost, bool stale,
                   bool pending, bool failedDownload, bool stop) {
  Clock clock;
  ConversationRuntimeState runtime(true, 63, "catalog-1");
  ConversationRuntimeSnapshot snapshot;
  assert(runtime.Read(snapshot));
  Transport transport(clock, runtime);
  transport.lostAcceptance = lost;
  transport.staleResult = stale;
  transport.foreverPending = pending;
  transport.failDownload = failedDownload;
  transport.stopInDownload = stop;
  TurnOperationObservation source;
  source.operationId = Id;
  source.kind = "turn";
  source.status = TurnOperationStatus::Completed;
  source.correlation = {"device-a", Id, Id, Id, 0, 0};
  source.reply = "Validated reply";
  source.decision = "ask";
  if (invalidSource) { source.correlation.clientTurnId = SpeechId; }
  const TurnCorrelation expected{"device-a", Id, Id, Id, 0, 0};
  std::vector<uint8_t> storage(64, 0);
  SpeechAudioBuffer buffer(storage.data(), storage.size());
  const auto outcome = PrepareSpeechOutput(Id, expected, source, snapshot, runtime,
      consent, 1000, Id, Id, "testToken01", transport, clock, buffer);
  if (!consent || invalidSource) {
    assert(outcome == SpeechOutputOutcome::Rejected && transport.submits == 0);
  } else {
    assert(transport.submits == (lost ? 2u : 1u) && transport.releases == 1);
    if (pending) {
      assert(outcome == SpeechOutputOutcome::Expired && transport.downloads == 0 && clock.now == 46000);
    } else if (stale || failedDownload || stop) {
      assert(outcome == (stop ? SpeechOutputOutcome::Cancelled : SpeechOutputOutcome::Failed));
      assert(!buffer.HasReadyPcm(clock.now, runtime));
    } else {
      assert(outcome == SpeechOutputOutcome::Ready && transport.polls == 2 && transport.downloads == 1);
      assert(buffer.HasReadyPcm(clock.now, runtime));
      uint8_t chunk[2] = {};
      assert(buffer.ReadPcm(0, chunk, sizeof(chunk), clock.now, runtime) == 2 && chunk[0] == 37);
    }
  }
}
}  // namespace
int main() {
  CheckScenario(true, false, false, false, false, false, false);
  CheckScenario(false, false, false, false, false, false, false);
  CheckScenario(true, true, false, false, false, false, false);
  CheckScenario(true, false, true, false, false, false, false);
  CheckScenario(true, false, false, true, false, false, false);
  CheckScenario(true, false, false, false, true, false, false);
  CheckScenario(true, false, false, false, false, true, false);
  CheckScenario(true, false, false, false, false, false, true);
}

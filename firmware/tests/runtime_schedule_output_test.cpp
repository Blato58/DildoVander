#include "../src/features/device_runtime/DeviceDiagnostics.h"
#include "../src/features/local_control/LocalControlOutput.h"
#include "../src/features/local_control/LocalControlTransport.h"
#include "../src/features/device_control/DeviceControl.h"
#include "../src/features/voice_turn/LocalVoicePort.h"
#include <cassert>
#include <iostream>

namespace {
class Journal final : public CommandJournal {
 public:
  bool HasPending() const override { return false; }
  uint8_t PendingCompartment() const override { return 0; }
  uint8_t ReadyMask() const override { return 63; }
  bool Begin(const std::string&, uint8_t) override { return false; }
  bool Resolve(uint8_t) override { return false; }
  bool PersistReadyMask(uint8_t) override { return false; }
};
class Safety final : public SafetyPort {
 public:
  void DisableActuators() override {}
  void StartVibration(uint8_t) override {}
  void ReleaseLatch(uint8_t) override {}
};
class Transport final : public LocalControlTransport {
 public:
  bool Send(const LocalSocketAdmission::Handle&, const std::string& bytes) override { json = bytes; return true; }
  void Close(const LocalSocketAdmission::Handle&) override {}
  std::string json;
};
class Voice final : public LocalVoicePort {
 public:
  LocalVoiceStatus VoiceStatus() const override { return {}; }
  bool ConfigureVoice(const std::string&, const std::string&) override { return false; }
  bool StartVoice(const std::string&, const ConversationRuntimeSnapshot&) override { return false; }
  void StopVoice() override {}
};
class Diagnostics final : public kufr::runtime::RuntimeDiagnosticsPort {
 public:
  bool ReadDiagnostics(kufr::runtime::DeviceDiagnosticSnapshot& snapshot) const override {
    ++reads;
    if (!available) return false;
    using H = kufr::runtime::DeviceHealth;
    for (auto& word : snapshot.health.values) word = UINT32_MAX;
    snapshot.health[H::SampledAtHigh] = 0;
    snapshot.health[H::Profile] = 2;
    snapshot.health[H::ActiveProfile] = 0;
    snapshot.health[H::StorageStartup] = 3;
    snapshot.health[H::StorageState] = 5;
    snapshot.health[H::JournalUsed] = 32;
    snapshot.health[H::MediaTermination] = 4;
    snapshot.runtime.timing = {UINT32_MAX, UINT32_MAX, UINT32_MAX, UINT32_MAX,
        UINT32_MAX, UINT32_MAX, UINT32_MAX, UINT32_MAX};
    snapshot.runtime.maximumIoTransferUs = UINT32_MAX;
    snapshot.runtime.maximumPassBusUs = UINT32_MAX;
    snapshot.runtime.ioLockRefusals = UINT32_MAX;
    snapshot.runtime.ioBudgetRefusals = UINT32_MAX;
    snapshot.runtime.coalescedLedFrames = UINT32_MAX;
    return true;
  }
  bool available = true;
  mutable unsigned reads = 0;
};
}

static void DiagnosticInputIsExplicitAndScopedToStateGet() {
  const std::string prefix = "{\"protocolVersion\":1,\"type\":\"state.get\",\"messageId\":\"c85743d5-1b72-41b8-8500-ae3c102b27d6\",\"deviceId\":\"kufr-001\",\"bootId\":\"c85743d5-1b72-41b8-8500-ae3c102b27d6\"";
  LocalControlMessage message;
  const auto parse = [&](const std::string& value) { return ParseLocalControlMessage(value.data(), value.size(), message); };
  assert(parse(prefix + "}"));
  assert(!message.includeDiagnostics);
  assert(parse(prefix + ",\"includeDiagnostics\":true}"));
  assert(message.includeDiagnostics && message.kind == LocalControlMessageKind::StateGet);
  assert(!parse(prefix + ",\"includeDiagnostics\":false}"));
  assert(!parse(prefix + ",\"includeDiagnostics\":true,\"includeDiagnostics\":true}"));
  auto stop = prefix; stop.replace(stop.find("state.get"), 9, "safety.stop");
  assert(!parse(stop + ",\"includeDiagnostics\":true}"));
}

int main() {
  DiagnosticInputIsExplicitAndScopedToStateGet();
  constexpr char boot[] = "c85743d5-1b72-41b8-8500-ae3c102b27d6";
  constexpr char conversationId[] = "ac665612-864c-4482-bb68-7415921119ae";
  Journal journal;
  Safety safetyPort;
  SafetyController safety(safetyPort);
  SelectionSequence selection(safety);
  DeviceControl control("kufr-001", boot, selection, journal, 1);
  ConversationSession conversation(boot, conversationId);
  Transport transport;
  Voice voice;
  LocalControlOutput output("kufr-001", boot, control, journal, safety, selection, conversation, transport, &voice);
  Diagnostics diagnostics;
  output.BindDiagnostics(diagnostics);
  assert(output.Snapshot({7, 1}, "", 100));
  assert(diagnostics.reads == 0 && transport.json.find("\"diagnostics\"") == std::string::npos);
  assert(output.ScopedSnapshot({7, 1}, "", 101, false, true));
  assert(diagnostics.reads == 1 && transport.json.find("\"diagnostics\":{") != std::string::npos);
  assert(transport.json.size() > 2048 && transport.json.size() <= 4096);
  assert(output.ScopedSnapshot({7, 1}, "", 102, false, false));
  assert(diagnostics.reads == 1 && transport.json.find("\"diagnostics\"") == std::string::npos);
  diagnostics.available = false;
  assert(output.ScopedSnapshot({7, 1}, "", 103, false, true));
  assert(diagnostics.reads == 2 && transport.json.find("\"diagnostics\"") == std::string::npos);
  std::cout << "PASS negotiated diagnostics, legacy omission, bounded4096 envelope and unavailable sample\n";
}

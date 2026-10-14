#include "FileJournal.h"
#include "VirtualHardware.h"
#include "device_control/PersistentCommandJournal.h"
#include "physical_effects/PhysicalPresentation.h"
#include "dance/DanceCoordinator.h"
#include "conversation/ConversationTextCodec.h"
#include "conversation/ConversationStartCodec.h"
#include "conversation/TurnOperationCodec.h"

#include <algorithm>
#include <ctime>
#include <iomanip>
#include <iostream>
#include <limits>
#include <map>
#include <sstream>
#include <stdexcept>
#include <vector>

namespace {
std::string Json(const std::string& value) {
  std::string result = "\"";
  for (const char byte : value) {
    if (byte == '\\' || byte == '"') result += '\\';
    result += byte;
  }
  return result + '"';
}

bool Decimal(const std::string& value, uint64_t& result) {
  if (value.empty() || (value.size() > 1 && value[0] == '0')) return false;
  result = 0;
  for (char byte : value) {
    if (byte < '0' || byte > '9' || result > (UINT64_MAX - (byte - '0')) / 10) return false;
    result = result * 10 + static_cast<unsigned>(byte - '0');
  }
  return true;
}

bool Uuid(const std::string& value) {
  if (value.size() != 36) return false;
  for (size_t index = 0; index < value.size(); ++index) {
    const char byte = value[index];
    if (index == 8 || index == 13 || index == 18 || index == 23) {
      if (byte != '-') return false;
    } else if (!((byte >= '0' && byte <= '9') || (byte >= 'a' && byte <= 'f'))) return false;
  }
  return true;
}

bool DecodeHex(const std::string& encoded, size_t maximum, std::string& decoded) {
  decoded.clear();
  if (encoded.empty() || encoded.size() % 2 || encoded.size() > maximum * 2) return false;
  const auto digit = [](char byte) -> int {
    if (byte >= '0' && byte <= '9') return byte - '0';
    if (byte >= 'a' && byte <= 'f') return byte - 'a' + 10;
    if (byte >= 'A' && byte <= 'F') return byte - 'A' + 10;
    return -1;
  };
  decoded.reserve(encoded.size() / 2);
  for (size_t index = 0; index < encoded.size(); index += 2) {
    const int high = digit(encoded[index]), low = digit(encoded[index + 1]);
    if (high < 0 || low < 0) { decoded.clear(); return false; }
    decoded += static_cast<char>(high * 16 + low);
  }
  return true;
}

bool Identity(const std::string& value) {
  if (value.empty() || value.size() > 64) return false;
  for (char byte : value) {
    if (!((byte >= 'a' && byte <= 'z') || (byte >= 'A' && byte <= 'Z') ||
          (byte >= '0' && byte <= '9') || byte == '-' || byte == '_' || byte == '.')) return false;
  }
  return true;
}

bool CadHash(const std::string& value) {
  if (value.size() != 64) return false;
  for (char byte : value) {
    if (!((byte >= '0' && byte <= '9') || (byte >= 'a' && byte <= 'f'))) return false;
  }
  return true;
}

uint64_t Fingerprint(bool shortVariant) {
  uint64_t hash = 14695981039346656037ull;
  for (const char* byte = PresentationScore::BundleHash; *byte; ++byte) {
    hash ^= static_cast<unsigned char>(*byte);
    hash *= 1099511628211ull;
  }
  return hash ^ static_cast<uint64_t>(shortVariant);
}

class SafetyAdapter final : public SafetyPort {
 public:
  explicit SafetyAdapter(PhysicalEffects& effects) : effects_(effects) {}
  void DisableActuators() override { effects_.Disable(); }
  void StartVibration(uint8_t slot) override { if (!effects_.StartVibration(slot)) effects_.Fault(); }
  void ReleaseLatch(uint8_t slot) override { if (!effects_.Move(slot, true)) effects_.Fault(); }
  void LowerProp(uint8_t slot) override { if (!effects_.Move(slot, false)) effects_.Fault(); }
  uint32_t MotionTimeoutMs(uint8_t slot) const override { return effects_.MotionTimeoutMs(slot); }
  bool PropRaised(uint8_t slot) const override { return effects_.IsRaised(slot); }
  bool PropResting(uint8_t slot) const override { return effects_.IsResting(slot); }
 private:
  PhysicalEffects& effects_;
};

class Host final {
 public:
  Host(const std::string& instance, const std::string& boot,
       const std::string& journal, const std::string& build, const std::string& cad)
      : instance_(instance), boot_(boot), build_(build), cad_(cad), storage_(journal),
        journal_(storage_), effects_(io_), safetyPort_(effects_), safety_(safetyPort_),
        selection_(safety_, true), control_(instance, boot, selection_, journal_, 1),
        state_(false, 0, ""), runtime_(control_, selection_, state_, "ollivander-private-party-v1"),
        physical_(effects_, runtime_), presentation_(physical_), dance_(player_, effects_) {
    std::array<ServoCalibration, 6> calibration{};
    calibration.fill({1100, 1900, 500});
    if (!effects_.Configure(calibration, 1) || !runtime_.BindPresentation(presentation_)) {
      throw std::runtime_error("native-composition-failed");
    }
    // Establish a known accepted LED reset independently of recovery/timing.
    // This clears only the simulated strip and makes no servo pose assertion.
    if (!io_.Lights(std::array<uint8_t, 108>{})) throw std::runtime_error("native-led-initialization-failed");
    physical_.BindBootIdentity(boot_);
    physical_.BindAudio(audio_);
    dance_.BindBootIdentity(boot_);
    Pass(0);
  }

  std::string Execute(const std::string& line, bool oversized) {
    if (oversized) return Snapshot(false, "line-too-large");
    std::istringstream input(line);
    std::vector<std::string> words;
    std::string word;
    for (char byte : line) {
      if (static_cast<unsigned char>(byte) < 32 && byte != '\t') return Snapshot(false, "invalid-ascii");
      if (static_cast<unsigned char>(byte) > 126) return Snapshot(false, "invalid-ascii");
    }
    while (input >> word) words.push_back(word);
    if (words.empty()) return Snapshot(false, "invalid-command");
    const std::string& command = words[0];
    if (command == "INTEGRATION_CANCEL" && words.size() == 1) {
      integration_ = Integration{};
      pending_.Clear();
      return Snapshot(true, "integration-cancelled");
    }
    if (command == "INTEGRATION_PREPARE" && words.size() == 5) {
      uint64_t utc = 0;
      std::string text;
      if (!Uuid(words[1]) || !Uuid(words[2]) || !Decimal(words[3], utc) || utc > 4102444740ull ||
          !DecodeHex(words[4], 4000, text)) return Snapshot(false, "invalid-integration-input");
      return PrepareIntegration(words[1], words[2], utc, text);
    }
    if (command == "INTEGRATION_BIND" && words.size() == 2 && Uuid(words[1])) {
      if (!integration_.active) return Snapshot(false, "integration-not-pending");
      if (!integration_.operationId.empty()) return Snapshot(false, "integration-already-bound");
      if (!state_.Matches(integration_.snapshot)) {
        integration_ = Integration{};
        return Snapshot(false, "integration-snapshot-changed");
      }
      integration_.operationId = words[1];
      return Snapshot(true, "integration-bound");
    }
    if (command == "INTEGRATION_RESULT" && words.size() == 3) {
      uint64_t utc = 0;
      std::string body;
      if (!Decimal(words[1], utc) || !DecodeHex(words[2], 4096, body)) {
        return Snapshot(false, "invalid-integration-response");
      }
      return CompleteIntegration(utc, body);
    }
    if (command == "SNAPSHOT" && words.size() == 1) return Snapshot(true, "observed");
    if (command == "TICK" && words.size() == 2) {
      uint64_t target = 0;
      if (!Decimal(words[1], target) || target < now_ || target - now_ > 60000 ||
          target > 4102444800000ull) return Snapshot(false, "invalid-time");
      // A deterministic step advances the same 10 ms owner loop, not one late
      // pass that fabricates missed effects. Bound work per input to 6000 passes.
      while (now_ < target) Pass(std::min(target, now_ + 10));
      return Snapshot(true, "advanced");
    }
    if (command == "STOP" && words.size() == 1) {
      integration_ = Integration{};
      pending_.Clear();
      dance_.StopDance();
      effects_.Fault();
      SelectionStep step;
      runtime_.Stop(step);
      return Snapshot(true, "stopped-recovery-required");
    }
    if (command == "INSPECT" && words.size() == 1) {
      return Snapshot(Inspect(), "inspection");
    }
    if (command == "TEXT" && words.size() == 4) {
      uint64_t slot = 0;
      if (!Uuid(words[1]) || !Decimal(words[2], slot) || slot < 1 || slot > 6 ||
          (words[3] != "main" && words[3] != "short")) return Snapshot(false, "invalid-proposal");
      return Start(words[1], static_cast<uint8_t>(slot), words[3] == "short");
    }
    if (command == "FAULT" && words.size() == 3) {
      uint64_t slot = 0;
      if (!Decimal(words[2], slot) || slot > 6 ||
          (words[1] != "none" && words[1] != "servo" && words[1] != "led" &&
           words[1] != "journal" && words[1] != "jam") ||
          (words[1] == "jam" && slot == 0)) return Snapshot(false, "invalid-fault");
      io_.fault = words[1];
      io_.faultSlot = static_cast<uint8_t>(slot);
      storage_.failWrites = words[1] == "journal";
      if (words[1] == "none") io_.jammed.fill(false);
      if (words[1] == "jam") io_.jammed[slot - 1] = true;
      return Snapshot(true, "fault-configured");
    }
    if (command == "DANCE" && words.size() == 2 && Uuid(words[1])) {
      const bool accepted = !runtime_.HasAutonomousPresentation() &&
          !control_.NeedsRecovery() && dance_.StartDance(words[1]);
      if (accepted) {
        danceTrace_ = true;
        runtime_.SetExternalActivity(true);
        dance_.Tick(now_);
      }
      return Snapshot(accepted, accepted ? "synthetic-dance-admitted" : "dance-rejected");
    }
    if (command == "DANCE_STOP" && words.size() == 1) {
      dance_.StopDance();
      return Snapshot(true, "dance-stop-requested");
    }
    return Snapshot(false, "unsupported-command");
  }

 private:
  void Pass(uint64_t now) {
    io_.Advance(now);
    now_ = now;
    presentation_.CheckDeadline(now_);
    effects_.Tick(now_);
    SelectionStep step;
    runtime_.Tick(now_, step);
    presentation_.CheckDeadline(now_);
    physical_.Tick(now_);
    player_.Advance(now_);
    dance_.Tick(now_);
    runtime_.SetExternalActivity(dance_.Reserved());
  }

  bool Inspect() {
    if (dance_.Reserved() || runtime_.HasAutonomousPresentation() ||
        (presentation_.State() != WandPresentationState::Idle &&
         presentation_.State() != WandPresentationState::Fault)) return false;
    if (!effects_.RecoverAfterInspection() || !runtime_.RecoverAfterInspection()) return false;
    integration_ = Integration{};
    // INSPECT explicitly asserts all six props were put at rest and reloaded.
    // Preserve journal admission/history, using the real inventory owner.
    const std::string lease = "native-inspection";
    if (!runtime_.AcquireManualControl(lease, now_)) return false;
    bool healthy = true;
    for (uint8_t slot = 1; slot <= 6; ++slot) {
      InventoryReloadCommand command;
      command.commandId = "inspection-" + std::to_string(++inspectionSequence_);
      command.deviceId = instance_;
      command.bootId = boot_;
      command.controlLeaseId = lease;
      command.sequence = slot;
      command.expectedRevision = control_.Revision();
      command.issuedAtDeviceMs = now_;
      command.maxAgeMs = 2000;
      command.compartment = slot;
      command.inspectionConfirmed = true;
      healthy = runtime_.ConfirmReload(command, true, now_).outcome == CommandOutcome::Accepted && healthy;
    }
    // Production lease release deliberately latches Stop even when idle. The
    // same explicit inspection authorizes recovery after inventory is persisted.
    SelectionStep step;
    runtime_.DisconnectManualControl(lease, step);
    if (!healthy || !runtime_.RecoverAfterInspection()) { effects_.Fault(); return false; }
    io_.Inspect();
    return true;
  }

  std::string Start(const std::string& id, uint8_t slot, bool shortVariant) {
    const CommandJournal::Lookup lookup = journal_.Find(id, slot, Fingerprint(shortVariant));
    if (lookup != CommandJournal::Lookup::Missing) {
      return Snapshot(false, lookup == CommandJournal::Lookup::Same ? "duplicate" : "operation-conflict");
    }
    ConversationRuntimeSnapshot snapshot;
    if (!state_.Read(snapshot) || dance_.Reserved() || !physical_.SelectVariant(shortVariant)) {
      return Snapshot(false, "runtime-unavailable");
    }
    TurnCorrelation expected;
    expected.deviceId = instance_;
    expected.bootId = boot_;
    expected.conversationId = "native-synthetic-conversation";
    expected.clientTurnId = id;
    expected.generation = snapshot.generation;
    expected.deviceRevision = snapshot.deviceRevision;
    TurnOperationObservation observed;
    observed.operationId = id;
    observed.kind = "turn";
    observed.status = TurnOperationStatus::Completed;
    observed.correlation = expected;
    observed.reply = "Synthetic native proposal";
    observed.decision = "propose";
    observed.hasProposal = true;
    TurnProposal& proposal = observed.proposal;
    proposal.proposalId = id;
    proposal.conversationId = expected.conversationId;
    proposal.turnId = id;
    proposal.bootId = boot_;
    proposal.generation = snapshot.generation;
    proposal.deviceRevision = snapshot.deviceRevision;
    proposal.catalogVersion = snapshot.catalogVersion;
    proposal.compartment = slot;
    const uint64_t utcNow = 1791936000ull + now_ / 1000;
    const std::time_t expiry = static_cast<std::time_t>(utcNow + 31);
    std::tm utc{};
#ifdef _WIN32
    gmtime_s(&utc, &expiry);
#else
    gmtime_r(&expiry, &utc);
#endif
    char formatted[32]{};
    std::strftime(formatted, sizeof(formatted), "%Y-%m-%dT%H:%M:%SZ", &utc);
    proposal.expiresAt = formatted;
    if (EvaluateTurnOperationWithProposal(id, expected, snapshot.generation,
        snapshot.deviceRevision, snapshot.catalogVersion, snapshot.readyMask,
        utcNow, observed) != TurnPollDecision::ProposalReady ||
        !pending_.Store(proposal, snapshot, state_, now_, ProposalRemainingMs(proposal, utcNow))) {
      return Snapshot(false, "proposal-guard-rejected");
    }
    const bool accepted = presentation_.Start(pending_, state_, now_);
    if (accepted) danceTrace_ = false;
    return Snapshot(accepted, accepted ? "presentation-admitted" : "presentation-rejected");
  }

  std::string PrepareIntegration(const std::string& clientTurnId,
      const std::string& conversationId, uint64_t utc, const std::string& text) {
    if (integration_.active && utc <= integration_.deadlineUtc) return Snapshot(false, "integration-busy");
    integration_ = Integration{};
    ConversationRuntimeSnapshot snapshot;
    if (!state_.Read(snapshot) || dance_.Reserved() || !effects_.Healthy() ||
        presentation_.State() != WandPresentationState::Idle) return Snapshot(false, "runtime-unavailable");
    ConversationTextTurnRequest request;
    request.conversationId = conversationId;
    request.clientTurnId = request.idempotencyKey = clientTurnId;
    request.bootId = boot_;
    request.catalogVersion = snapshot.catalogVersion;
    request.generation = snapshot.generation;
    request.deviceRevision = snapshot.deviceRevision;
    request.readyMask = snapshot.readyMask;
    request.text = text;
    std::string body;
    if (!BuildConversationTextTurnRequest(request, body)) return Snapshot(false, "integration-request-invalid");
    integration_.active = true;
    integration_.snapshot = snapshot;
    integration_.startedUtc = utc;
    integration_.deadlineUtc = utc + 60;
    integration_.startedAtMs = now_;
    integration_.correlation.deviceId = instance_;
    integration_.correlation.bootId = boot_;
    integration_.correlation.conversationId = conversationId;
    integration_.correlation.clientTurnId = clientTurnId;
    integration_.correlation.generation = snapshot.generation;
    integration_.correlation.deviceRevision = snapshot.deviceRevision;
    std::string response = Snapshot(true, "integration-prepared");
    response.pop_back();
    // Sensitive text exists only in this one transport handoff. The parent
    // strips externalRequest before storing or publishing observer telemetry.
    response += ",\"externalRequest\":{\"body\":" + body + ",\"correlation\":{\"deviceId\":" + Json(instance_) +
        ",\"bootId\":" + Json(boot_) + ",\"conversationId\":" + Json(conversationId) +
        ",\"clientTurnId\":" + Json(clientTurnId) + ",\"generation\":" + std::to_string(snapshot.generation) +
        ",\"deviceRevision\":" + std::to_string(snapshot.deviceRevision) +
        ",\"catalogVersion\":" + Json(snapshot.catalogVersion) + "}}}";
    return response;
  }

  std::string CompleteIntegration(uint64_t utc, const std::string& body) {
    if (!integration_.active) return Snapshot(false, "integration-not-pending");
    if (integration_.operationId.empty()) return Snapshot(false, "integration-not-bound");
    if (utc < integration_.startedUtc || utc > integration_.deadlineUtc ||
        now_ < integration_.startedAtMs || now_ - integration_.startedAtMs > 60000 ||
        !state_.Matches(integration_.snapshot)) {
      integration_ = Integration{};
      return Snapshot(false, "integration-stale");
    }
    TurnOperationObservation observed;
    if (!ParseTurnOperation(body.data(), body.size(), observed)) return Snapshot(false, "integration-codec-rejected");
    const TurnPollDecision decision = EvaluateTurnOperationWithProposal(
        integration_.operationId, integration_.correlation, integration_.snapshot.generation,
        integration_.snapshot.deviceRevision, integration_.snapshot.catalogVersion,
        integration_.snapshot.readyMask, utc, observed);
    if (observed.status != TurnOperationStatus::Completed) return Snapshot(false, "integration-not-completed");
    const ConversationRuntimeSnapshot snapshot = integration_.snapshot;
    integration_ = Integration{};
    if (decision == TurnPollDecision::ReplyReady) return Snapshot(true, "integration-reply-ready-no-audio");
    if (decision != TurnPollDecision::ProposalReady ||
        !pending_.Store(observed.proposal, snapshot, state_, now_, ProposalRemainingMs(observed.proposal, utc))) {
      return Snapshot(false, "integration-proposal-rejected");
    }
    const bool accepted = presentation_.Start(pending_, state_, now_);
    if (accepted) danceTrace_ = false;
    return Snapshot(accepted, accepted ? "integration-presentation-admitted" : "integration-presentation-rejected");
  }

  std::string Snapshot(bool accepted, const std::string& code) {
    std::ostringstream out;
    const WandPresentationState state = presentation_.State();
    // A pending durable record is expected during an owned presentation. It
    // becomes recovery evidence when ownership is lost, stopped or rebooted.
    const bool recovery = selection_.NeedsRecovery() || !effects_.Healthy() ||
        (!runtime_.HasAutonomousPresentation() && control_.NeedsRecovery());
    const char* phase = recovery ? "recovery-required" : "idle";
    if (!recovery) {
      if (dance_.Reserved()) phase = "dance";
      if (state == WandPresentationState::Raising) phase = "raising";
      if (state == WandPresentationState::Showing) phase = "showing";
      if (state == WandPresentationState::Lowering) phase = "lowering";
    }
    out << "{\"schemaVersion\":1,\"source\":{\"mode\":\"simulation\",\"instanceId\":" << Json(instance_)
        << ",\"deviceId\":" << Json(instance_) << ",\"bootId\":" << Json(boot_)
        << ",\"sessionId\":" << Json(boot_) << ",\"firmwareVersion\":\"source-build\",\"buildId\":" << Json(build_)
        << ",\"hardwareProfile\":\"B-effects\",\"sceneBundleHash\":" << Json(PresentationScore::BundleHash)
        << ",\"cadManifestRevision\":" << (cad_.empty() ? "null" : Json(cad_))
        << ",\"calibrationRevision\":\"1\"},\"sequence\":" << Json(std::to_string(++sequence_))
        << ",\"timeMs\":" << Json(std::to_string(now_)) << ",\"revision\":" << Json(std::to_string(control_.Revision()))
        << ",\"reported\":{\"state\":" << Json(phase) << ",\"slot\":" << unsigned(presentation_.Compartment())
        << ",\"readyMask\":" << unsigned(control_.ReadyMask()) << ",\"generation\":" << Json(std::to_string(runtime_.Generation()))
        << ",\"needsRecovery\":" << (recovery ? "true" : "false")
        << ",\"physicalVerification\":\"unavailable\",\"servo\":[";
    for (uint8_t slot = 1; slot <= 6; ++slot) {
      if (slot > 1) out << ',';
      const char* estimate = !effects_.Healthy() ? "unknown" : effects_.IsRaised(slot) ? "raised"
          : effects_.IsResting(slot) ? "rest" : "moving";
      out << "{\"slot\":" << unsigned(slot) << ",\"pulseUs\":";
      if (io_.pulse[slot - 1]) out << io_.pulse[slot - 1]; else out << "null";
      out << ",\"estimate\":" << Json(estimate) << '}';
    }
    out << "],\"outputsEnabled\":" << (io_.enabled ? "true" : "false") << ",\"ledGrb\":[";
    for (size_t index = 0; index < io_.led.size(); ++index) {
      if (index) out << ',';
      out << unsigned(io_.led[index]);
    }
    out << "],\"vibrationMask\":" << unsigned(io_.vibrationMask)
        << ",\"audio\":" << Json(dance_.Reserved() ? "simulated-track-enqueued" :
            danceTrace_ && player_.Status().phase == "completed" ? "simulated-track-drained" :
            audio_.active ? "simulated-tone-enqueued" : "simulated-tone-idle")
        << "},\"model\":";
    if (!io_.poseKnown) out << "null";
    else {
      out << "{\"slots\":[";
      for (size_t index = 0; index < io_.position.size(); ++index) {
        if (index) out << ',';
        out << "{\"slot\":" << index + 1 << ",\"position\":" << std::setprecision(8) << io_.position[index]
            << ",\"jammed\":" << (io_.jammed[index] ? "true" : "false") << '}';
      }
      out << "],\"evidence\":\"simulated\"}";
    }
    out << ",\"trace\":" << ExportPresentationTrace(
            danceTrace_ ? dance_.Diagnostics() : physical_.Diagnostics())
        << ",\"transport\":{\"droppedSnapshots\":\"0\"},\"capabilities\":[\"observe\",\"presentation\",\"synthetic-text\",\"simulated-tones\",\"dance-synthetic\",\"integration-codecs\"]"
        << ",\"commandResult\":{\"accepted\":" << (accepted ? "true" : "false") << ",\"code\":" << Json(code)
        << ",\"orderingHealthy\":" << (io_.orderingHealthy ? "true" : "false")
        << ",\"servoWrites\":" << Json(std::to_string(io_.servoWrites))
        << ",\"vibrationWrites\":" << Json(std::to_string(io_.vibrationWrites))
        << ",\"lightWrites\":" << Json(std::to_string(io_.lightWrites)) << "}}";
    return out.str();
  }

  std::string instance_, boot_, build_, cad_;
  struct Integration {
    bool active = false;
    std::string operationId;
    TurnCorrelation correlation;
    ConversationRuntimeSnapshot snapshot;
    uint64_t startedUtc = 0, deadlineUtc = 0, startedAtMs = 0;
  } integration_;
  uint64_t now_ = 0, sequence_ = 0, inspectionSequence_ = 0;
  FileJournal storage_;
  PersistentCommandJournal journal_;
  VirtualHardware io_;
  PhysicalEffects effects_;
  SafetyAdapter safetyPort_;
  SafetyController safety_;
  SelectionSequence selection_;
  DeviceControl control_;
  ConversationRuntimeState state_;
  DeviceControlRuntime runtime_;
  PhysicalPresentation physical_;
  WandPresentation presentation_;
  PendingProposal pending_;
  SimulatedTones audio_;
  bool danceTrace_ = false;
  SimulatedDancePlayer player_;
  kufr::dance::DanceCoordinator dance_;
};
}

int main(int argc, char** argv) {
  try {
    std::map<std::string, std::string> arguments;
    for (int index = 1; index < argc; index += 2) {
      if (index + 1 >= argc || (std::string(argv[index]) != "--instance" &&
          std::string(argv[index]) != "--boot" && std::string(argv[index]) != "--journal" &&
          std::string(argv[index]) != "--build" && std::string(argv[index]) != "--cad") || arguments.count(argv[index])) {
        throw std::runtime_error("invalid-arguments");
      }
      arguments[argv[index]] = argv[index + 1];
    }
    if ((arguments.size() != 4 && arguments.size() != 5) || !Identity(arguments["--instance"]) ||
        !Identity(arguments["--boot"]) || !Identity(arguments["--build"]) ||
        arguments["--journal"].empty()) throw std::runtime_error("required-identity-arguments");
    if (arguments.count("--cad") && !CadHash(arguments["--cad"])) throw std::runtime_error("invalid-cad-hash");
    const std::string cad = arguments.count("--cad") ? arguments["--cad"] : "";
    Host host(arguments["--instance"], arguments["--boot"], arguments["--journal"], arguments["--build"], cad);
    // Consume oversize records without accumulating them; retain request/response
    // alignment even when a client sends a megabyte followed by valid commands.
    std::string line;
    bool oversized = false;
    char byte = 0;
    while (std::cin.get(byte)) {
      if (byte == '\n') {
        if (!line.empty() && line.back() == '\r') line.pop_back();
        std::cout << host.Execute(line, oversized) << '\n' << std::flush;
        line.clear();
        oversized = false;
      } else if (line.size() < 16384) line += byte;
      else oversized = true;
    }
    if (!line.empty() || oversized) std::cout << host.Execute(line, oversized) << '\n' << std::flush;
    return 0;
  } catch (const std::exception& error) {
    std::cerr << error.what() << '\n';
    return 1;
  }
}

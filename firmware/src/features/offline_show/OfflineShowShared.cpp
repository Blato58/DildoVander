#include "OfflineShow.h"

#include <algorithm>
#include <cstring>
#include <limits>

#include "OfflineEvidenceBridge.h"

namespace kufr { namespace offline {
namespace {
class UnusedStorage final : public StoragePort {
 public:
  bool Read(Outbox&) override { return false; }
  bool Write(const Outbox&) override { return false; }
};
StoragePort& NoLegacyStorage() { static UnusedStorage storage; return storage; }
bool CopyId(std::array<char, 37>& destination, const std::string& source) {
  if (source.size() != 36) return false;
  for (size_t index = 0; index != 36; ++index) {
    const char value = source[index];
    if ((index == 8 || index == 13 || index == 18 || index == 23) ? value != '-'
        : !((value >= '0' && value <= '9') || (value >= 'a' && value <= 'f'))) return false;
  }
  std::copy(source.begin(), source.end(), destination.begin());
  destination[36] = 0;
  return true;
}
bool Retained(OfflineEvidenceBridge::Transfer value) {
  return value == OfflineEvidenceBridge::Transfer::RetainedVolatile ||
      value == OfflineEvidenceBridge::Transfer::AlreadyRetained;
}
}

struct OfflineShow::SharedImplementation final : SharedState {
  using Bridge = OfflineEvidenceBridge;
  using Status = Bridge::Status;
  using Operation = EvidenceStorageOwner::Operation;
  enum class Phase { Idle, JournalReserve, EvidenceReserve, Audio, Begin, Presenting, Completion };
  enum class CommandKind { Arm, Quiet, Ambient, Settings, Stop, Leave };
  struct Command {
    EvidenceStorageOwner::Request request;
    Bridge::IntentHandle handle;
    CommandKind kind = CommandKind::Quiet;
    Status status = Status::Pending;
    uint64_t first = 0, second = 0, generation = 0, revision = 0;
    bool a = false, b = false, occupied = false, finalized = false, observed = false, observerAbandoned = false;
  };
  struct AckIntent {
    Outcome outcome;
    Bridge::Handle handle;
    Status status = Status::Pending;
    bool occupied = false;
  };

  SharedImplementation(OfflineShow& show, Bridge& bridge) : show(show), bridge(bridge) {
    show.storageHealthy_ = bridge.Bootstrap();
    Refresh();
  }
  bool Active() const override { return phase != Phase::Idle; }
  bool LastPending() const override { return lastPending; }
  bool HasWork() const override { return Active() || correctiveStop || bridge.HasWork(); }
  bool Fresh(const Command& command) const {
    return command.generation == show.runtime_.Generation() && command.revision == show.control_.Revision() &&
        !show.control_.NeedsRecovery() && !Active();
  }
  void Refresh() {
    Outbox publication;
    show.storageHealthy_ = !bridge.IsPoisoned() && bridge.Read(publication);
    if (!show.storageHealthy_) {
      show.quiet_ = true;
      show.ambientEnabled_ = show.armed_ = false;
      return;
    }
    show.outbox_ = publication;
    show.quiet_ = publication.quiet || safeQuiet;
    show.ambientEnabled_ = publication.ambientEnabled && !safeAmbient;
  }
  Command* Existing(CommandKind kind, uint64_t first, uint64_t second, bool a, bool b) {
    for (auto& lane : commands) for (auto& command : lane)
      if (command.occupied && !command.observed && command.kind == kind && command.first == first && command.second == second &&
          command.a == a && command.b == b) return &command;
    return nullptr;
  }
  bool Result(Command& command, bool observe = true) {
    lastPending = command.status == Status::Pending;
    if (observe && !lastPending) command.observed = true;
    return command.status == Status::Succeeded;
  }
  void Finalize(Command& command) {
    if (command.status == Status::Pending || command.finalized) return;
    command.finalized = true;
    if (command.status != Status::Succeeded) return;
    const bool relaxing = (command.request.setEnabled && command.request.enabled) ||
        (command.request.setQuiet && !command.request.quiet) ||
        (command.request.setAmbient && command.request.ambient);
    if (relaxing && !Fresh(command)) { command.status = Status::Refused; return; }
    if (command.kind == CommandKind::Arm && Fresh(command) && show.runtime_.OfflineAuthority() &&
        show.outbox_.offlineEnabled && !safeDisabled) show.armed_ = true;
    if (command.kind == CommandKind::Quiet && !command.a && Fresh(command)) safeQuiet = false;
    if (command.kind == CommandKind::Ambient && command.a && Fresh(command)) safeAmbient = false;
    if (command.kind == CommandKind::Settings && Fresh(command)) {
      if (!command.request.quiet) safeQuiet = false;
      if (command.request.ambient) safeAmbient = false;
    }
  }
  Command* Queue(CommandKind kind, Bridge::Lane lane, uint64_t first, uint64_t second,
      bool a, bool b, const EvidenceStorageOwner::Request& request) {
    if (auto existing = Existing(kind, first, second, a, b)) return existing;
    auto& entries = commands[static_cast<size_t>(lane)];
    for (auto& command : entries) {
      if (command.occupied && (!command.observed || command.handle.generation)) continue;
      command = Command();
      command.occupied = true; command.kind = kind; command.first = first; command.second = second;
      command.a = a; command.b = b; command.request = request;
      command.generation = show.runtime_.Generation(); command.revision = show.control_.Revision();
      return &command;
    }
    lastPending = true; // The original transport message remains the third owner.
    return nullptr;
  }
  bool SubmitCommand(Command& command, Bridge::Lane lane, bool observe = true) {
    if (command.status != Status::Pending) return Result(command, observe);
    if (!command.handle.generation) {
      const auto retained = bridge.TryQueueSettings(lane, command.request, command.handle);
      if (retained == Bridge::Transfer::Rejected) command.status = Status::Refused;
    }
    lastPending = command.status == Status::Pending;
    return Result(command, observe);
  }
  EvidenceStorageOwner::Request SettingsRequest() const {
    EvidenceStorageOwner::Request request;
    request.operation = Operation::OfflineSettings;
    request.fenceStopRevision = true;
    request.expectedStopRevision = show.outbox_.serverStopRevision;
    return request;
  }
  void Pump() {
    const bool previousPending = lastPending;
    bridge.Pump();
    Refresh();
    for (size_t lane = 0; lane != commands.size(); ++lane) for (auto& command : commands[lane]) {
      if (!command.occupied) continue;
      if (command.status == Status::Pending) {
        if (!command.handle.generation) SubmitCommand(command, static_cast<Bridge::Lane>(lane), false);
        if (command.handle.generation) command.status = bridge.PollSettings(command.handle);
      }
      Finalize(command);
      if (command.status != Status::Pending && (command.observerAbandoned ||
          (command.kind == CommandKind::Stop &&
           (command.first < desiredStopRevision || command.second != stopGeneration)))) command.observed = true;
      if (command.status != Status::Pending && command.handle.generation && bridge.ReleaseSettings(command.handle))
        command.handle = {};
    }
    for (auto& ack : acknowledgements) {
      if (!ack.occupied || !ack.handle.generation) continue;
      ack.status = bridge.PollAck(ack.handle);
      if (ack.status == Status::Succeeded && bridge.Release(ack.handle)) ack.handle = {};
    }
    for (auto& retired : retiredCandidates) if (retired.generation && bridge.Release(retired)) retired = {};
    EnsureStop();
    Refresh();
    lastPending = previousPending;
  }
  bool Arm(uint64_t generation, uint64_t revision) override {
    lastPending = false;
    if (auto command = Existing(CommandKind::Arm, generation, revision, false, false)) return Result(*command);
    if (correctiveStop) { lastPending = true; return false; }
    if (show.armed_ && generation == show.runtime_.Generation() && revision == show.control_.Revision()) return true;
    if (Active() || generation != show.runtime_.Generation() || revision != show.control_.Revision() ||
        std::strcmp(show.Preflight(), "ready") || !show.runtime_.SetOfflineAuthority(true)) return false;
    EvidenceStorageOwner::Request request;
    request.operation = Operation::OfflineEnabled; request.setEnabled = request.enabled = true;
    request.fenceStopRevision = true; request.expectedStopRevision = show.outbox_.serverStopRevision;
    auto command = Queue(CommandKind::Arm, Bridge::Lane::Enabled, generation, revision, false, false, request);
    if (!command) return false;
    safeDisabled = false;
    return SubmitCommand(*command, Bridge::Lane::Enabled);
  }
  bool Quiet(bool enabled) override {
    lastPending = false;
    if (enabled) { safeQuiet = show.quiet_ = true; show.CancelAmbient(); }
    if (!show.storageHealthy_) return false;
    if (auto command = Existing(CommandKind::Quiet, 0, 0, enabled, false)) return Result(*command);
    if (!enabled && (show.control_.NeedsRecovery() || Active())) return false;
    auto request = SettingsRequest(); request.setQuiet = true; request.quiet = enabled;
    auto command = Queue(CommandKind::Quiet, Bridge::Lane::Settings, 0, 0, enabled, false, request);
    return command && SubmitCommand(*command, Bridge::Lane::Settings);
  }
  bool Ambient(bool enabled) override {
    lastPending = false;
    show.CancelAmbient(); show.idleAtMs_ = 0;
    if (!enabled) { safeAmbient = true; show.ambientEnabled_ = false; }
    if (!show.storageHealthy_ || (enabled && (show.deviceId_.empty() || show.control_.NeedsRecovery() || Active()))) return false;
    if (auto command = Existing(CommandKind::Ambient, 0, 0, enabled, false)) return Result(*command);
    auto request = SettingsRequest(); request.setAmbient = true; request.ambient = enabled;
    auto command = Queue(CommandKind::Ambient, Bridge::Lane::Settings, 0, 0, enabled, false, request);
    return command && SubmitCommand(*command, Bridge::Lane::Settings);
  }
  bool Settings(uint64_t revision, bool quiet, bool ambient) override {
    lastPending = false;
    if (auto command = Existing(CommandKind::Settings, revision, 0, quiet, ambient)) return Result(*command);
    if (revision <= show.outbox_.serverSettingsRevision) return true;
    if (!show.storageHealthy_) return false;
    const bool blocked = show.deviceId_.empty() || show.control_.NeedsRecovery() || Active();
    auto request = SettingsRequest(); request.serverSettingsRevision = revision;
    request.setQuiet = request.setAmbient = true;
    request.quiet = quiet || blocked; request.ambient = ambient && !blocked;
    if (request.quiet) { safeQuiet = show.quiet_ = true; show.CancelAmbient(); }
    if (!request.ambient) { safeAmbient = true; show.ambientEnabled_ = false; show.CancelAmbient(); }
    auto command = Queue(CommandKind::Settings, Bridge::Lane::Settings, revision, 0, quiet, ambient, request);
    return command && SubmitCommand(*command, Bridge::Lane::Settings);
  }
  void Abort() {
    if (candidate.generation) {
      bridge.Cancel(candidate);
      for (auto& retired : retiredCandidates) if (!retired.generation) { retired = candidate; break; }
      candidate = {};
    }
    show.runtime_.CancelOfflinePreparation();
    show.runtime_.CancelClaimedAutonomousPresentation();
    show.runtime_.ReleaseDecoration(original.generation);
    show.pending_.Clear();
    phase = Phase::Idle;
    startAccepted = false;
  }
  void StopRam() {
    SelectionStep step;
    show.runtime_.Stop(step); // Safety always precedes bridge or audio work.
    stopGeneration = show.runtime_.Generation();
    show.armed_ = false; safeDisabled = safeQuiet = safeAmbient = true;
    autoArmPending = false;
    for (auto& lane : commands) for (auto& command : lane)
      if (command.occupied && command.kind != CommandKind::Stop) command.observerAbandoned = true;
    show.quiet_ = true; show.ambientEnabled_ = false;
    show.audio_.Cancel(); show.CancelAmbient();
    Abort();
  }
  void EnsureStop() {
    if (!correctiveStop || !show.storageHealthy_) return;
    auto completed = Existing(CommandKind::Stop, desiredStopRevision, stopGeneration, false, false);
    if (((completed && completed->status == Status::Succeeded) || show.outbox_.serverStopRevision > desiredStopRevision) &&
        show.outbox_.serverStopRevision >= desiredStopRevision &&
        !show.outbox_.offlineEnabled && !show.outbox_.ambientEnabled && show.outbox_.quiet) {
      if (completed) completed->observed = true;
      correctiveStop = false; return;
    }
    auto request = SettingsRequest();
    request.fenceStopRevision = false;
    request.setQuiet = request.quiet = request.setAmbient = request.setEnabled = true;
    request.ambient = request.enabled = false;
    request.serverStopRevision = desiredStopRevision > show.outbox_.serverStopRevision ? desiredStopRevision : 0;
    auto command = Queue(CommandKind::Stop, Bridge::Lane::Stop, desiredStopRevision, stopGeneration, false, false, request);
    if (command) SubmitCommand(*command, Bridge::Lane::Stop, false);
  }
  void Stop() override {
    lastPending = false;
    StopRam(); correctiveStop = true;
    EnsureStop();
    lastPending = correctiveStop;
  }
  bool ServerStop(uint64_t revision) override {
    lastPending = false;
    if (revision <= show.outbox_.serverStopRevision) return true;
    if (!correctiveStop || revision > desiredStopRevision) {
      StopRam(); desiredStopRevision = std::max(desiredStopRevision, revision); correctiveStop = true;
    }
    EnsureStop(); lastPending = true;
    return false;
  }
  bool Leave() override {
    lastPending = false;
    if (auto command = Existing(CommandKind::Leave, 0, 0, false, false)) return Result(*command);
    if (Active() || !show.PrepareInteraction() || !show.runtime_.SetOfflineAuthority(false)) return false;
    show.armed_ = false; safeDisabled = true;
    EvidenceStorageOwner::Request request;
    request.operation = Operation::OfflineEnabled; request.setEnabled = true; request.enabled = false;
    auto command = Queue(CommandKind::Leave, Bridge::Lane::Enabled, 0, 0, false, false, request);
    return command && SubmitCommand(*command, Bridge::Lane::Enabled);
  }
  bool SameStart(const std::string& id, uint8_t slot, bool shortVariant, uint64_t generation,
      uint64_t revision, bool scripted, uint64_t epoch) const {
    return id == original.operationId.data() && slot == original.slot && shortVariant == original.shortVariant &&
        generation == original.generation && revision == original.deviceRevision && scripted == original.scripted &&
        epoch == original.operationEpoch;
  }
  bool Start(const std::string& id, uint8_t slot, bool shortVariant, uint64_t generation,
      uint64_t revision, uint64_t now, bool scripted, uint64_t epoch) override {
    lastPending = false;
    if (Active()) {
      if (!SameStart(id, slot, shortVariant, generation, revision, scripted, epoch)) return false;
      Advance(now); lastPending = Active() && !startAccepted; return startAccepted;
    }
    for (const auto& record : show.outbox_.records) if (id == record.operationId.data() && epoch == record.operationEpoch)
      return record.completed && record.slot == slot && record.shortVariant == shortVariant &&
          record.generation == generation && record.deviceRevision == revision && record.scripted == scripted;
    if (!show.armed_ || slot < 1 || slot > 6 || !epoch || generation != show.runtime_.Generation() ||
        revision != show.control_.Revision() || std::strcmp(show.Preflight(), "ready") ||
        !show.state_.Read(snapshot) || snapshot.generation != generation || snapshot.deviceRevision != revision ||
        snapshot.operationEpoch != epoch || !show.physical_.SelectVariant(shortVariant)) return false;
    show.CancelAmbient();
    if (show.audio_.Busy()) return false;
    original = Outcome();
    if (!CopyId(original.operationId, id) || !CopyId(original.bootId, show.bootId_) ||
        !CopyId(original.localEveningId, show.localEveningId_)) return false;
    original.slot = slot; original.generation = generation; original.deviceRevision = revision;
    original.operationEpoch = epoch; original.startedAtMs = now; original.scripted = scripted;
    original.shortVariant = shortVariant;
    original.physicalFingerprint = PhysicalPresentation::OperationFingerprint(shortVariant);
    calibration = show.physical_.CurrentCalibrationRevision();
    candidate = {}; completionCaptured = proposalStored = startAccepted = false;
    phase = Phase::JournalReserve;
    Advance(now); lastPending = Active() && !startAccepted;
    return startAccepted;
  }
  bool StartNext(const std::string& supplied, uint64_t now, bool generate) override {
    if (Active()) {
      if (!generate && supplied != original.operationId.data()) { lastPending = false; return false; }
      Advance(now); lastPending = Active() && !startAccepted; return startAccepted;
    }
    std::string id = supplied;
    if (generate) {
      if (show.scriptedSequence_ == std::numeric_limits<uint64_t>::max()) return false;
      const uint64_t sequence = ++show.scriptedSequence_;
      id = show.bootId_;
      if (id.size() != 36) return false;
      const char* digits = "0123456789abcdef";
      for (unsigned index = 0; index != 12; ++index) {
        const char current = id[35 - index];
        const unsigned value = current <= '9' ? current - '0' : current - 'a' + 10;
        if (value > 15) return false;
        id[35 - index] = digits[value ^ ((sequence >> (index * 4)) & 15)];
      }
    }
    for (unsigned count = 0; count != 6; ++count) {
      const uint8_t slot = show.nextSlot_;
      show.nextSlot_ = static_cast<uint8_t>(show.nextSlot_ % 6 + 1);
      if (show.control_.ReadyMask() & (1u << (slot - 1)))
        return Start(id, slot, false, show.runtime_.Generation(), show.control_.Revision(), now, true,
            show.control_.OperationEpoch());
    }
    lastPending = false; return false;
  }
  bool PreparationLive(uint64_t now) const {
    return now >= original.startedAtMs && now - original.startedAtMs <= 18500 &&
        original.generation == show.runtime_.Generation() && original.deviceRevision == show.control_.Revision() &&
        original.operationEpoch == show.control_.OperationEpoch() && calibration == show.physical_.CurrentCalibrationRevision() &&
        !show.control_.HasAutonomousCompletionFault() && show.storageHealthy_;
  }
  void Advance(uint64_t now) {
    if (!Active()) return;
    if (original.generation != show.runtime_.Generation() || !show.storageHealthy_ || show.audio_.Failed()) { Stop(); return; }
    if ((phase == Phase::JournalReserve || phase == Phase::EvidenceReserve || phase == Phase::Audio || phase == Phase::Begin) &&
        !PreparationLive(now)) { Stop(); return; }
    if (phase == Phase::JournalReserve) {
      const auto status = show.runtime_.ReserveOfflinePreparation(original.operationId.data(), original.slot,
          snapshot, original.physicalFingerprint, calibration, original.startedAtMs, now);
      if (status == DeviceControlRuntime::PresentationProgress::Pending) return;
      if (status != DeviceControlRuntime::PresentationProgress::Succeeded) { Stop(); return; }
      phase = Phase::EvidenceReserve;
    }
    if (phase == Phase::EvidenceReserve) {
      if (!candidate.generation) {
        const auto transfer = bridge.Reserve(original, candidate);
        if (transfer == Bridge::Transfer::Busy) return;
        if (!Retained(transfer)) { Stop(); return; }
      }
      const auto status = bridge.PollReserve(candidate);
      if (status == Bridge::ReserveStatus::Pending) return;
      if (status != Bridge::ReserveStatus::DurableReserved || !PreparationLive(now)) { Stop(); return; }
      const std::string clipId = "slot-" + std::to_string(original.slot);
      const Clip* slotClip = show.FindClip(clipId.c_str());
      const Clip* opening = original.shortVariant ? slotClip : show.FindClip(original.slot % 2 ? "intro-1" : "intro-2");
      if (!slotClip || !slotClip->pcm || slotClip->slot != original.slot || !opening || !opening->pcm ||
          !show.runtime_.ReserveDecoration(original.generation) || !show.audio_.Play(*opening, original.generation)) {
        Stop(); return;
      }
      show.runtime_.FinishOfflinePreparation();
      audioStage = original.shortVariant ? 1 : 0;
      phase = Phase::Audio; startAccepted = true;
      return;
    }
    if (phase == Phase::Audio) {
      if (show.audio_.Busy()) return;
      if (audioStage == 0) {
        const std::string clipId = "slot-" + std::to_string(original.slot);
        const Clip* clip = show.FindClip(clipId.c_str());
        if (!clip || !show.audio_.Play(*clip, original.generation)) { Stop(); return; }
        audioStage = 1; return;
      }
      show.runtime_.ReleaseDecoration(original.generation);
      ConversationRuntimeSnapshot current;
      if (!show.state_.Read(current) || current.generation != original.generation ||
          current.deviceRevision != original.deviceRevision || current.operationEpoch != original.operationEpoch ||
          current.catalogVersion != snapshot.catalogVersion) { Stop(); return; }
      TurnProposal proposal;
      proposal.proposalId = original.operationId.data(); proposal.bootId = show.bootId_;
      proposal.catalogVersion = current.catalogVersion; proposal.compartment = original.slot;
      proposal.generation = original.generation; proposal.deviceRevision = original.deviceRevision;
      proposal.operationEpoch = original.operationEpoch;
      if (!show.pending_.Store(proposal, current, show.state_, now, 2000)) { Stop(); return; }
      proposalStored = true; phase = Phase::Begin;
    }
    if (phase == Phase::Begin) {
      if (!proposalStored) { Stop(); return; }
      if (!show.runtime_.StartOfflinePresentationPolled(show.pending_, now)) {
        if (show.presentation_.AdmissionIsPending()) return;
        Stop(); return;
      }
      phase = Phase::Presenting; return;
    }
    if (phase == Phase::Presenting) {
      if (show.presentation_.State() == WandPresentationState::Fault) { Stop(); return; }
      if (show.presentation_.State() != WandPresentationState::Idle) return;
      if (!show.control_.WasTimedPresentationCompleted(original.operationId.data(), original.slot,
          original.operationEpoch, original.physicalFingerprint)) { Stop(); return; }
      completedAt = now; completionCaptured = true; phase = Phase::Completion;
    }
    if (phase == Phase::Completion) {
      if (!completionCaptured || !Retained(bridge.Complete(candidate, completedAt))) { Stop(); return; }
      const auto result = bridge.PollComplete(candidate);
      if (result == Status::Pending) return;
      if (result != Status::Succeeded) { Stop(); return; }
      phase = Phase::Idle;
      show.idleAtMs_ = now; show.ambientGeneration_ = show.runtime_.Generation();
      const Clip* ending = show.FindClip(original.slot % 2 ? "ending-1" : "ending-2");
      if (ending && ending->pcm && show.runtime_.ReserveDecoration(show.ambientGeneration_)) {
        if (show.audio_.Play(*ending, show.ambientGeneration_)) show.ambientAudio_ = true;
        else show.runtime_.ReleaseDecoration(show.ambientGeneration_);
      }
    }
  }
  bool Ack(const std::string& id, uint64_t epoch) override {
    lastPending = false;
    if (Active() || !show.storageHealthy_) return false;
    for (auto& ack : acknowledgements) if (ack.occupied && id == ack.outcome.operationId.data() && epoch == ack.outcome.operationEpoch) {
      lastPending = ack.status == Status::Pending; return ack.status == Status::Succeeded;
    }
    const Outcome* published = nullptr;
    for (const auto& record : show.outbox_.records) if (record.operationEpoch == epoch && id == record.operationId.data()) published = &record;
    if (!published) return true;
    if (!published->completed) return false;
    for (auto& ack : acknowledgements) {
      if (ack.occupied && (ack.status == Status::Pending || ack.handle.generation)) continue;
      ack = AckIntent(); ack.occupied = true; ack.outcome = *published;
      if (!bridge.Find(*published, ack.handle) || !Retained(bridge.Ack(ack.handle, *published))) {
        ack.occupied = false; return false;
      }
      lastPending = true; return false;
    }
    lastPending = true; return false;
  }
  void Tick(uint64_t now, bool idle) override {
    Pump();
    if (Active()) { Advance(now); return; }
    if (!show.armed_ && !safeDisabled && show.outbox_.offlineEnabled && idle && !show.control_.NeedsRecovery())
    {
      if (!autoArmPending) {
        autoArmGeneration = show.runtime_.Generation(); autoArmRevision = show.control_.Revision();
        autoArmPending = true;
      }
      Arm(autoArmGeneration, autoArmRevision);
      if (!lastPending) autoArmPending = false;
    }
    show.TickAmbient(now, idle);
  }

  OfflineShow& show;
  Bridge& bridge;
  std::array<std::array<Command, 2>, 3> commands{};
  std::array<AckIntent, 8> acknowledgements{};
  std::array<Bridge::Handle, 8> retiredCandidates{};
  Phase phase = Phase::Idle;
  Outcome original;
  ConversationRuntimeSnapshot snapshot;
  Bridge::Handle candidate;
  uint64_t calibration = 0, completedAt = 0, desiredStopRevision = 0, stopGeneration = 0;
  uint64_t autoArmGeneration = 0, autoArmRevision = 0;
  uint8_t audioStage = 0;
  bool lastPending = false, startAccepted = false, completionCaptured = false, proposalStored = false;
  bool safeQuiet = false, safeAmbient = false, safeDisabled = false, correctiveStop = false;
  bool autoArmPending = false;
};

OfflineShow::OfflineShow(DeviceControlRuntime& runtime, ConversationRuntimeState& state,
    DeviceControl& control, WandPresentation& presentation, PhysicalPresentation& physical,
    AudioPort& audio, OfflineEvidenceBridge& evidence, const std::string& deviceId,
    const std::string& bootId, const std::string& localEveningId, const BankView* bank)
    : OfflineShow(runtime, state, control, presentation, physical, audio, NoLegacyStorage(),
        deviceId, bootId, localEveningId, bank) {
  shared_ = std::make_shared<SharedImplementation>(*this, evidence);
}
} }

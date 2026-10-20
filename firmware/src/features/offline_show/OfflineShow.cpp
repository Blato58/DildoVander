#include "OfflineShow.h"

#include <algorithm>
#include <cstring>

#include "OfflineBank.generated.h"
#include "../../protocol/JsonWriter.h"

namespace kufr { namespace offline {
namespace {
void String(const std::string& value, std::string& json) { AppendJsonString(value, json); }
template<size_t N> bool Copy(std::array<char, N>& target, const std::string& source) {
  if (source.empty() || source.size() >= N) return false;
  std::copy(source.begin(), source.end(), target.begin());
  target[source.size()] = 0;
  return true;
}
bool ValidId(const std::array<char, 37>& id) {
  if (id[36] != 0) return false;
  for (size_t index = 0; index < 36; ++index) {
    if (index == 8 || index == 13 || index == 18 || index == 23) {
      if (id[index] != '-') return false;
    } else if (!((id[index] >= '0' && id[index] <= '9') ||
                 (id[index] >= 'a' && id[index] <= 'f'))) return false;
  }
  return true;
}
}

const char* OfflineShow::CatalogVersion() { return "ollivander-private-party-v1"; }
const BankView& CompiledBank() {
  static const BankView view{bank::Clips, sizeof(bank::Clips) / sizeof(Clip),
      bank::TotalBytes, bank::CapacityBytes, bank::Complete, bank::Version, OfflineShow::CatalogVersion(), bank::Voice};
  return view;
}

OfflineShow::OfflineShow(DeviceControlRuntime& runtime, ConversationRuntimeState& state,
    DeviceControl& control, WandPresentation& presentation, PhysicalPresentation& physical,
    AudioPort& audio, StoragePort& storage, const std::string& deviceId,
    const std::string& bootId, const std::string& localEveningId, const BankView* bank)
    : runtime_(runtime), state_(state), control_(control), presentation_(presentation),
      physical_(physical), sceneBridge_(physical), audio_(audio), storage_(storage), deviceId_(deviceId),
      bootId_(bootId), localEveningId_(localEveningId), bank_(bank ? *bank : CompiledBank()) {
  storageHealthy_ = storage_.Read(outbox_) && outbox_.version == 1;
  ambientEnabled_ = storageHealthy_ && outbox_.ambientEnabled;
  quiet_ = outbox_.quiet;
  for (const auto& record : outbox_.records) {
    if (record.operationId[0] && (!ValidId(record.operationId) || !ValidId(record.bootId) ||
        !ValidId(record.localEveningId) || record.slot < 1 || record.slot > 6 ||
        (record.completed && record.completedAtMs < record.startedAtMs))) storageHealthy_ = false;
  }
}

size_t OfflineShow::Count() const {
  size_t count = 0;
  for (const auto& record : outbox_.records) if (record.operationId[0]) ++count;
  return count;
}

const Clip* OfflineShow::FindClip(const char* id) const {
  for (size_t index = 0; index < bank_.count; ++index) {
    const auto& candidate = bank_.clips[index];
    if (std::strcmp(candidate.id, id) == 0) return &candidate;
  }
  return nullptr;
}

const Clip* OfflineShow::ApprovedClip(const std::string& id) const {
  if (!bank_.complete || !voiceCompatible_ || !audio_.Available()) return nullptr;
  const auto* clip = FindClip(id.c_str());
  return clip && clip->pcm && clip->bytes && clip->sha256[0] ? clip : nullptr;
}

const char* OfflineShow::Preflight() const {
  if (deviceId_.empty() || !storageHealthy_ || control_.NeedsRecovery() || presentation_.State() == WandPresentationState::Fault)
    return "recovery_required";
  ConversationRuntimeSnapshot snapshot;
  if (std::string(bank_.catalogVersion) != CatalogVersion() || (state_.Read(snapshot) && snapshot.catalogVersion != CatalogVersion())) return "incompatible_catalog";
  if (!bank_.complete || bank_.totalBytes > bank_.capacityBytes || !audio_.Available() || !voiceCompatible_) return "missing_assets";
  if (control_.CalibrationRevision() == 0) return "calibration_required";
  if (Count() == outbox_.records.size() || !control_.ReadyMask() || !control_.CanRecordOperation()) return "audio_only";
  return "ready";
}

bool OfflineShow::Arm(uint64_t generation, uint64_t revision) {
  if (Active() || generation != runtime_.Generation() || revision != control_.Revision() ||
      std::strcmp(Preflight(), "ready") || !runtime_.SetOfflineAuthority(true)) return false;
  if (!outbox_.offlineEnabled) {
    Outbox updated = outbox_;
    updated.offlineEnabled = true;
    if (!storage_.Write(updated)) { storageHealthy_ = false; return false; }
    outbox_ = updated;
  }
  armed_ = true;
  idleAtMs_ = 0;
  return true;
}

bool OfflineShow::Start(const std::string& operationId, uint8_t slot, bool shortVariant,
    uint64_t generation, uint64_t revision, uint64_t nowMs, bool scripted) {
  if (operationId.size() != 36 || slot < 1 || slot > 6) return false;
  for (const auto& previous : outbox_.records) {
    if (operationId == previous.operationId.data()) return previous.slot == slot &&
        previous.shortVariant == shortVariant && previous.generation == generation && previous.scripted == scripted;
  }
  if (runtime_.FindOperation(operationId, slot, 0) != CommandJournal::Lookup::Missing) return false;
  if (!armed_ || Active() || generation != runtime_.Generation() || revision != control_.Revision() ||
      std::strcmp(Preflight(), "ready") || !(control_.ReadyMask() & (1u << (slot - 1)))) return false;
  CancelAmbient();
  if (audio_.Busy()) return false;
  const std::string id = "slot-" + std::to_string(slot);
  const Clip* clip = FindClip(id.c_str());
  if (!clip || !clip->pcm || !clip->bytes || clip->slot != slot ||
      !runtime_.ReserveDecoration(generation)) return false;
  const Clip* opening = shortVariant ? clip : FindClip(slot % 2 ? "intro-1" : "intro-2");
  if (!opening || !opening->pcm) { runtime_.ReleaseDecoration(generation); return false; }
  size_t index = 0;
  while (index < outbox_.records.size() && outbox_.records[index].operationId[0]) ++index;
  if (index == outbox_.records.size()) { runtime_.ReleaseDecoration(generation); return false; }
  Outbox updated = outbox_;
  auto& record = updated.records[index];
  if (!Copy(record.operationId, operationId) || !Copy(record.bootId, bootId_) ||
      !Copy(record.localEveningId, localEveningId_) || !ValidId(record.operationId) ||
      !ValidId(record.bootId) || !ValidId(record.localEveningId)) {
    runtime_.ReleaseDecoration(generation); return false;
  }
  record.generation = generation; record.startedAtMs = nowMs; record.slot = slot;
  record.scripted = scripted; record.shortVariant = shortVariant;
  if (!storage_.Write(updated)) {
    storageHealthy_ = false; runtime_.ReleaseDecoration(generation); return false;
  }
  outbox_ = updated;
  if (!audio_.Play(*opening, generation)) { runtime_.ReleaseDecoration(generation); return false; }
  activeIndex_ = static_cast<int>(index);
  motionStarted_ = false;
  audioStage_ = shortVariant ? 1 : 0;
  return true;
}

bool OfflineShow::StartNext(const std::string& operationId, uint64_t nowMs) {
  for (unsigned count = 0; count < 6; ++count) {
    const uint8_t slot = nextSlot_;
    nextSlot_ = static_cast<uint8_t>(nextSlot_ % 6 + 1);
    if (control_.ReadyMask() & (1u << (slot - 1)))
      return Start(operationId, slot, false, runtime_.Generation(), control_.Revision(), nowMs, true);
  }
  return false;
}

bool OfflineShow::StartNext(uint64_t nowMs) {
  if (bootId_.size() != 36 || scriptedSequence_ >= 0xffffffffffffull) return false;
  // Boot UUID is generated while hardware entropy is explicitly enabled.
  // A boot-scoped monotonic sequence needs neither RF nor a fresh RNG lease.
  const uint64_t sequence = ++scriptedSequence_;
  std::string id = bootId_;
  const char* digits = "0123456789abcdef";
  for (unsigned index = 0; index < 12; ++index) {
    const char current = id[35 - index];
    const unsigned value = current <= '9' ? current - '0' : current - 'a' + 10;
    if (value > 15) return false;
    id[35 - index] = digits[value ^ ((sequence >> (index * 4)) & 15)];
  }
  return StartNext(id, nowMs);
}

bool OfflineShow::CompleteRecord(uint64_t nowMs) {
  Outbox updated = outbox_;
  updated.records[activeIndex_].completed = true;
  updated.records[activeIndex_].completedAtMs = nowMs;
  if (!storage_.Write(updated)) { quiet_ = true; storageHealthy_ = false; return false; }
  outbox_ = updated;
  return true;
}

void OfflineShow::CancelAmbient() {
  if (ambientAudio_) audio_.Cancel();
  sceneBridge_.CancelResponse("ambient", ambientGeneration_);
  if (ambientAudio_ && !audio_.Busy()) {
    runtime_.ReleaseDecoration(ambientGeneration_);
    ambientAudio_ = false;
  }
  ambientLight_ = false;
}

bool OfflineShow::Quiet(bool enabled) {
  quiet_ = enabled;
  if (!Active()) CancelAmbient();
  if (!storageHealthy_) { quiet_ = true; return false; }
  Outbox updated = outbox_; updated.quiet = enabled;
  if (!storage_.Write(updated)) { quiet_ = true; storageHealthy_ = false; return false; }
  outbox_ = updated;
  return true;
}

bool OfflineShow::ConfigureAmbient(bool enabled) {
  if (enabled && (deviceId_.empty() || !storageHealthy_ || control_.NeedsRecovery() || Active())) return false;
  CancelAmbient();
  ambientEnabled_ = enabled;
  idleAtMs_ = 0;
  if (!storageHealthy_) { ambientEnabled_ = false; return false; }
  Outbox updated = outbox_; updated.ambientEnabled = enabled;
  if (!storage_.Write(updated)) { ambientEnabled_ = false; storageHealthy_ = false; return false; }
  outbox_ = updated;
  return true;
}

bool OfflineShow::ApplyServerSettings(uint64_t revision, bool quiet, bool ambient) {
  if (revision <= outbox_.serverSettingsRevision) return true;
  if (!storageHealthy_) return false;
  const bool blocked = deviceId_.empty() || control_.NeedsRecovery() || Active();
  Outbox updated = outbox_;
  updated.serverSettingsRevision = revision;
  updated.quiet = quiet || blocked;
  updated.ambientEnabled = ambient && !blocked;
  if (!storage_.Write(updated)) { storageHealthy_ = false; return false; }
  outbox_ = updated;
  quiet_ = updated.quiet; ambientEnabled_ = updated.ambientEnabled;
  if (quiet_ || !ambientEnabled_) CancelAmbient();
  idleAtMs_ = 0;
  return true;
}

bool OfflineShow::ApplyServerStop(uint64_t revision) {
  if (revision <= outbox_.serverStopRevision) return false;
  Stop(); // Existing sticky physical Stop precedes persistence and network cancellation.
  if (storageHealthy_) {
    Outbox updated = outbox_; updated.serverStopRevision = revision;
    if (storage_.Write(updated)) outbox_ = updated;
    else storageHealthy_ = false;
  }
  return true;
}

void OfflineShow::Stop() {
  SelectionStep step;
  runtime_.Stop(step); // Physical outputs stop before audio or NVS work.
  quiet_ = true; armed_ = false; ambientEnabled_ = false;
  audio_.Cancel();
  sceneBridge_.CancelResponse("ambient", ambientGeneration_);
  if (ambientAudio_) runtime_.ReleaseDecoration(ambientGeneration_);
  ambientAudio_ = false;
  if (Active() && !motionStarted_) runtime_.ReleaseDecoration(outbox_.records[activeIndex_].generation);
  pending_.Clear();
  activeIndex_ = -1;
  if (storageHealthy_ && (outbox_.offlineEnabled || outbox_.ambientEnabled || !outbox_.quiet)) {
    Outbox updated = outbox_; updated.offlineEnabled = false;
    updated.ambientEnabled = false; updated.quiet = true;
    if (storage_.Write(updated)) outbox_ = updated;
    else storageHealthy_ = false;
  }
}

bool OfflineShow::Leave() {
  if (Active()) return false;
  if (!PrepareInteraction()) return false;
  if (!runtime_.SetOfflineAuthority(false)) return false;
  armed_ = false;
  if (outbox_.offlineEnabled) {
    Outbox updated = outbox_; updated.offlineEnabled = false;
    if (storage_.Write(updated)) outbox_ = updated;
    else { storageHealthy_ = false; return false; }
  }
  return true;
}

bool OfflineShow::PrepareInteraction() {
  if (Active()) return false;
  CancelAmbient();
  idleAtMs_ = 0;
  return !audio_.Busy();
}

void OfflineShow::Tick(uint64_t nowMs, bool idle) {
  // A persisted opt-in can resume only after the existing physical inspection
  // has recovered this boot. A pending journal or sticky Stop remains a gate.
  if (!armed_ && outbox_.offlineEnabled && idle && !control_.NeedsRecovery())
    Arm(runtime_.Generation(), control_.Revision());
  if (Active()) {
    auto& record = outbox_.records[activeIndex_];
    if (!motionStarted_) {
      if (runtime_.Generation() != record.generation || audio_.Failed() ||
          nowMs < record.startedAtMs || nowMs - record.startedAtMs > 18500) { Stop(); return; }
      if (audio_.Busy()) return;
      if (audioStage_ == 0) {
        const std::string clipId = "slot-" + std::to_string(record.slot);
        const Clip* clip = FindClip(clipId.c_str());
        if (!clip || !clip->pcm || !audio_.Play(*clip, record.generation)) { Stop(); return; }
        audioStage_ = 1;
        return;
      }
      runtime_.ReleaseDecoration(record.generation);
      ConversationRuntimeSnapshot snapshot;
      TurnProposal proposal;
      if (!state_.Read(snapshot)) { Stop(); return; }
      proposal.proposalId = record.operationId.data(); proposal.compartment = record.slot;
      proposal.bootId = bootId_; proposal.catalogVersion = snapshot.catalogVersion;
      proposal.generation = snapshot.generation; proposal.deviceRevision = snapshot.deviceRevision;
      if (!physical_.SelectVariant(record.shortVariant) ||
          !pending_.Store(proposal, snapshot, state_, nowMs, 2000) ||
          !runtime_.StartOfflinePresentation(pending_, nowMs)) { Stop(); return; }
      motionStarted_ = true;
      return;
    }
    if (presentation_.State() == WandPresentationState::Fault) { Stop(); return; }
    if (presentation_.State() != WandPresentationState::Idle) return;
    // The physical owner only returns Idle after its durable, calibrated timing
    // completion gate. Audio completion alone can never produce an outcome.
    if (!control_.WasTimedPresentationCompleted(record.operationId.data(), record.slot) ||
        !CompleteRecord(nowMs)) { Stop(); return; }
    const Clip* ending = FindClip(record.slot % 2 ? "ending-1" : "ending-2");
    activeIndex_ = -1;
    idleAtMs_ = nowMs; ambientLight_ = false;
    ambientGeneration_ = runtime_.Generation();
    if (ending && ending->pcm && runtime_.ReserveDecoration(ambientGeneration_)) {
      if (audio_.Play(*ending, ambientGeneration_)) ambientAudio_ = true;
      else runtime_.ReleaseDecoration(ambientGeneration_);
    }
    return;
  }
  if (!idle || control_.NeedsRecovery() || quiet_ || !ambientEnabled_ || !storageHealthy_) {
    if (!Active()) CancelAmbient();
    idleAtMs_ = nowMs;
    return;
  }
  if (!idleAtMs_ || nowMs < idleAtMs_) { idleAtMs_ = nowMs; return; }
  if (audio_.Busy()) return;
  if (ambientAudio_) runtime_.ReleaseDecoration(ambientGeneration_);
  ambientAudio_ = false;
  const uint64_t elapsed = nowMs - idleAtMs_;
  if (!ambientLight_ && elapsed >= 45000) {
    ambientGeneration_ = runtime_.Generation();
    SceneIntentContext context;
    context.bootId = bootId_; context.sessionId = "ambient-" + std::to_string(nowMs);
    context.responseId = "ambient"; context.sessionEpoch = ambientGeneration_;
    context.responseGeneration = ambientGeneration_; context.deviceGeneration = ambientGeneration_;
    context.validUntilDeviceMs = nowMs + 500;
    sceneBridge_.SetContext(context);
    SceneIntent intent;
    intent.intentId = "offline-ambient"; intent.bootId = bootId_; intent.sessionId = context.sessionId;
    intent.responseId = context.responseId; intent.sessionEpoch = context.sessionEpoch;
    intent.responseGeneration = context.responseGeneration; intent.kind = SceneIntentKind::Ambient;
    intent.issuedAtDeviceMs = nowMs; intent.validUntilDeviceMs = context.validUntilDeviceMs;
    ambientLight_ = sceneBridge_.Accept(intent, nowMs) == SceneIntentResult::Started;
  }
  if (elapsed < 120000 || (lastSpokenAtMs_ && nowMs - lastSpokenAtMs_ < 180000)) return;
  size_t recent = 0;
  for (size_t index = 0; index < spokenCount_; ++index)
    if (nowMs >= spokenAt_[index] && nowMs - spokenAt_[index] < 600000) ++recent;
  if (recent >= 3 || !audio_.Available() || !voiceCompatible_) return;
  const std::string id = "ambient-" + std::to_string(nextAmbient_ % 12 + 1);
  const Clip* clip = FindClip(id.c_str());
  ambientGeneration_ = runtime_.Generation();
  if (clip && clip->pcm && clip->durationMs <= 8000 && runtime_.ReserveDecoration(ambientGeneration_)) {
    if (audio_.Play(*clip, ambientGeneration_)) {
      ++nextAmbient_; lastSpokenAtMs_ = nowMs;
      ambientAudio_ = true;
      spokenAt_[spokenCursor_++ % 3] = nowMs;
      spokenCount_ = std::min<size_t>(spokenCount_ + 1, 3);
    } else runtime_.ReleaseDecoration(ambientGeneration_);
  }
}

bool OfflineShow::Acknowledge(const std::string& operationId) {
  if (!storageHealthy_ || Active()) return false;
  for (size_t index = 0; index < outbox_.records.size(); ++index) {
    if (operationId != outbox_.records[index].operationId.data()) continue;
    if (!outbox_.records[index].completed) return false;
    Outbox updated = outbox_;
    updated.records[index] = {};
    if (!storage_.Write(updated)) { storageHealthy_ = false; return false; }
    outbox_ = updated;
    return true;
  }
  return true; // An acknowledgement replay cannot recreate a record.
}

std::string OfflineShow::StatusJson() const {
  std::string json = "{\"state\":\"" + std::string(Preflight()) + "\",\"armed\":" + (armed_ ? "true" : "false") +
      ",\"quiet\":" + (quiet_ ? "true" : "false") + ",\"ambientEnabled\":" + (ambientEnabled_ ? "true" : "false") +
      ",\"generation\":" + std::to_string(runtime_.Generation()) +
      ",\"readyMask\":" + std::to_string(control_.ReadyMask()) + ",\"outboxCount\":" + std::to_string(Count()) +
      ",\"catalogVersion\":\"" + CatalogVersion() + "\",\"bankVersion\":\"" + bank_.version +
      "\",\"bankVoice\":\"" + bank_.voice + "\",\"voiceCompatible\":" + (voiceCompatible_ ? "true" : "false") +
      ",\"audioBytes\":" + std::to_string(bank_.totalBytes) + ",\"audioCapacityBytes\":" +
      std::to_string(bank_.capacityBytes) + ",\"streamWorkingBytes\":960,\"missing\":[";
  bool first = true;
  for (size_t index = 0; index < bank_.count; ++index) {
    const auto& candidate = bank_.clips[index];
    if (candidate.pcm && candidate.bytes && candidate.sha256[0]) continue;
    if (!first) json += ',';
    first = false; String(candidate.id, json);
  }
  if (!storageHealthy_) { if (!first) json += ','; json += "\"outbox_storage\""; first = false; }
  else if (Count() == outbox_.records.size()) { if (!first) json += ','; json += "\"outbox_capacity\""; first = false; }
  else if (!control_.CanRecordOperation()) { if (!first) json += ','; json += "\"journal_capacity\""; first = false; }
  if (!voiceCompatible_) { if (!first) json += ','; json += "\"voice_profile_marin_required\""; }
  return json + "]}";
}

std::string OfflineShow::OutboxJson() const {
  std::string json = "[";
  bool first = true;
  if (!storageHealthy_) return "[]";
  for (const auto& record : outbox_.records) {
    if (!record.operationId[0] || !record.completed) continue;
    if (!first) json += ',';
    first = false;
    json += "{\"operationId\":\"" + std::string(record.operationId.data()) + "\",\"deviceId\":";
    String(deviceId_, json);
    json += ",\"bootId\":\"" + std::string(record.bootId.data()) + "\",\"localEveningId\":\"" +
        record.localEveningId.data() + "\",\"eveningId\":null,\"generation\":" + std::to_string(record.generation) +
        ",\"sourceMode\":\"" + (record.scripted ? "offline_scripted" : "operator") + "\",\"compartment\":" +
        std::to_string(record.slot) + ",\"catalogVersion\":\"" + CatalogVersion() +
        "\",\"startedAtDeviceMs\":" + std::to_string(record.startedAtMs) + ",\"completedAtDeviceMs\":" +
        std::to_string(record.completedAtMs) + ",\"timeConfidence\":\"boot_monotonic\",\"outcome\":\"completed_by_timing\"}";
  }
  return json + "]";
}
} }

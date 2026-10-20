#pragma once

#include <array>
#include <string>

#include "../device_control/DeviceControlRuntime.h"
#include "../physical_effects/PhysicalPresentation.h"
#include "../scene_intent/SceneIntentBridge.h"

namespace kufr { namespace offline {
enum class ClipFormat { PcmS16Le, ImaAdpcm4, PcmS16Le32k };

struct Clip {
  const char* id;
  const char* text;
  const uint8_t* pcm;
  size_t bytes;
  uint32_t durationMs;
  const char* sha256;
  uint8_t slot;
  ClipFormat format = ClipFormat::PcmS16Le;
  uint32_t samples = 0;
};

struct BankView {
  const Clip* clips;
  size_t count, totalBytes, capacityBytes;
  bool complete;
  const char* version;
  const char* catalogVersion;
  const char* voice;
};
const BankView& CompiledBank();

class AudioPort {
 public:
  virtual ~AudioPort() = default;
  virtual bool Available() const = 0;
  virtual bool Play(const Clip& clip, uint64_t epoch) = 0;
  virtual void Cancel() = 0;
  virtual bool Busy() const = 0;
  virtual bool Failed() const = 0;
};

// Fixed-size local technical records only. Reserved records survive reset and
// are never exported as completed visits or silently evicted for capacity.
struct Outcome {
  std::array<char, 37> operationId{}, bootId{}, localEveningId{};
  uint64_t generation = 0, startedAtMs = 0, completedAtMs = 0;
  uint8_t slot = 0;
  bool completed = false, scripted = false, shortVariant = false;
  uint64_t operationEpoch = 0, physicalFingerprint = 0, deviceRevision = 0;
};
struct Outbox {
  uint32_t version = 2;
  bool offlineEnabled = false;
  bool ambientEnabled = false, quiet = false;
  uint64_t serverSettingsRevision = 0, serverStopRevision = 0;
  std::array<Outcome, 8> records{};
};
// The original NVS blob used this POD layout. Keep it distinct from v2 so a
// firmware upgrade never reads old bytes with the enlarged Outcome stride.
namespace storage {
struct LegacyOutcomeV1 {
  std::array<char, 37> operationId{}, bootId{}, localEveningId{};
  uint64_t generation = 0, startedAtMs = 0, completedAtMs = 0;
  uint8_t slot = 0;
  bool completed = false, scripted = false, shortVariant = false;
};
struct LegacyOutboxV1 {
  uint32_t version = 1;
  bool offlineEnabled = false;
  bool ambientEnabled = false, quiet = false;
  uint64_t serverSettingsRevision = 0, serverStopRevision = 0;
  std::array<LegacyOutcomeV1, 8> records{};
};
bool DecodeOutboxBytes(const void* bytes, size_t length, Outbox& outbox);
}
class StoragePort {
 public:
  virtual ~StoragePort() = default;
  virtual bool Read(Outbox& outbox) = 0;
  virtual bool Write(const Outbox& outbox) = 0;
};

// No GPIO owner: every motion uses PendingProposal -> WandPresentation ->
// PhysicalPresentation -> the existing persistent command journal and ports.
class OfflineShow final {
 public:
  OfflineShow(DeviceControlRuntime& runtime, ConversationRuntimeState& state,
      DeviceControl& control, WandPresentation& presentation,
      PhysicalPresentation& physical, AudioPort& audio, StoragePort& storage,
      const std::string& deviceId, const std::string& bootId,
      const std::string& localEveningId, const BankView* bank = nullptr);
  const char* Preflight() const;
  bool Arm(uint64_t generation, uint64_t revision);
  bool Start(const std::string& operationId, uint8_t slot, bool shortVariant,
      uint64_t generation, uint64_t revision, uint64_t nowMs, bool scripted = false,
      uint64_t operationEpoch = 0);
  bool StartNext(const std::string& operationId, uint64_t nowMs);
  bool StartNext(uint64_t nowMs);
  void Tick(uint64_t nowMs, bool idle);
  bool Quiet(bool enabled);
  bool ConfigureAmbient(bool enabled);
  bool ApplyServerSettings(uint64_t revision, bool quiet, bool ambient);
  bool ApplyServerStop(uint64_t revision);
  uint64_t ServerStopRevision() const { return outbox_.serverStopRevision; }
  uint64_t ServerSettingsRevision() const { return outbox_.serverSettingsRevision; }
  bool QuietEnabled() const { return quiet_; }
  bool AmbientEnabled() const { return ambientEnabled_; }
  const Clip* ApprovedClip(const std::string& id) const;
  void SetVoice(const std::string& voice) { voiceCompatible_ = voice == bank_.voice; }
  void Stop();
  bool Leave();
  bool PrepareInteraction();
  bool AmbientActive() const { return ambientAudio_ || (ambientLight_ && !Active() && runtime_.HasDecoration()); }
  bool Acknowledge(const std::string& operationId, uint64_t operationEpoch = 0);
  bool Armed() const { return armed_; }
  bool Active() const { return activeIndex_ >= 0; }
  std::string StatusJson() const;
  std::string OutboxJson(bool includeOperationEpoch = false) const;
  static const char* CatalogVersion();
 private:
  size_t Count() const;
  const Clip* FindClip(const char* id) const;
  bool CompleteRecord(uint64_t nowMs);
  void CancelAmbient();
  DeviceControlRuntime& runtime_;
  ConversationRuntimeState& state_;
  DeviceControl& control_;
  WandPresentation& presentation_;
  PhysicalPresentation& physical_;
  SceneIntentBridge sceneBridge_;
  AudioPort& audio_;
  StoragePort& storage_;
  std::string deviceId_, bootId_, localEveningId_;
  Outbox outbox_{};
  const BankView& bank_;
  PendingProposal pending_;
  bool storageHealthy_ = false, armed_ = false, quiet_ = false;
  bool ambientLight_ = false;
  bool ambientEnabled_ = false;
  bool voiceCompatible_ = true;
  bool ambientAudio_ = false;
  bool motionStarted_ = false;
  uint8_t audioStage_ = 0;
  int activeIndex_ = -1;
  uint8_t nextSlot_ = 1, nextAmbient_ = 0;
  uint64_t idleAtMs_ = 0, lastSpokenAtMs_ = 0, ambientGeneration_ = 0;
  std::array<uint64_t, 3> spokenAt_{};
  size_t spokenCount_ = 0;
  size_t spokenCursor_ = 0;
  uint64_t scriptedSequence_ = 0;
};

} }

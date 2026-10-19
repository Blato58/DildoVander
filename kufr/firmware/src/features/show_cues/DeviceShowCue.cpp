#include "DeviceShowCue.h"
#include "../../protocol/JsonCursor.h"
#include "../../protocol/JsonWriter.h"
#include "../conversation/ConversationStartCodec.h"
#include "../conversation/ConversationSession.h"
#include <utility>

namespace {
using kufr_protocol::JsonCursor;
constexpr uint64_t Maximum = 9007199254740991ull;
bool Number(JsonCursor& cursor, uint64_t& value) { return cursor.ReadUnsigned(value) && value <= Maximum; }
bool Uuid(JsonCursor& cursor, std::string& value) { return cursor.ReadString(value, 36, true) && IsConversationUuid(value); }
bool Cue(JsonCursor& cursor, DeviceShowCue& cue) {
  if (!cursor.Consume('{')) return false;
  uint32_t seen = 0;
  do {
    std::string key;
    if (!cursor.ReadString(key, 32, true) || !cursor.Consume(':')) return false;
    uint32_t bit = 0;
    bool valid = false;
    if (key == "cueId") { bit = 1; valid = Uuid(cursor, cue.cueId); }
    else if (key == "duelId") { bit = 2; valid = cursor.ReadNull() || Uuid(cursor, cue.duelId); }
    else if (key == "round") { bit = 4; uint64_t value = 0; valid = Number(cursor, value) && value <= 3; cue.round = value; }
    else if (key == "bootId") { bit = 8; valid = Uuid(cursor, cue.bootId); }
    else if (key == "generation") { bit = 16; valid = Number(cursor, cue.generation); }
    else if (key == "deviceRevision") { bit = 32; valid = Number(cursor, cue.deviceRevision); }
    else if (key == "issuedAtDeviceMs") { bit = 64; valid = Number(cursor, cue.issuedAtMs); }
    else if (key == "validUntilDeviceMs") { bit = 128; valid = Number(cursor, cue.validUntilMs); }
    else if (key == "sceneId") { bit = 256; valid = cursor.ReadString(cue.sceneId, 16, true) &&
        (cue.sceneId == "consider" || cue.sceneId == "ambient" || cue.sceneId == "wake" || cue.sceneId == "farewell"); }
    else if (key == "compartment") { bit = 512; uint64_t value = 0; valid = Number(cursor, value) && value >= 1 && value <= 6; cue.compartment = value; }
    else if (key == "speechText") { bit = 1024; valid = cursor.ReadString(cue.speechText, 2000) && IsValidConversationUtf8(cue.speechText, 500, true); }
    else if (key == "audioPath") { bit = 2048; valid = cursor.ReadNull() || cursor.ReadString(cue.audioPath, 100, true); }
    else if (key == "clipId") { bit = 4096; valid = cursor.ReadNull() || cursor.ReadString(cue.clipId, 40, true); }
    else if (key == "cueType") { bit = 8192; valid = cursor.ReadString(cue.cueType, 24, true) &&
        (cue.cueType == "NarratedBeat" || cue.cueType == "Combined" || cue.cueType == "LedPreset" || cue.cueType == "PlayLocalClip" || cue.cueType == "VibratePreset"); }
    else if (key == "preset") { bit = 16384; valid = cursor.ReadNull() || cursor.ReadString(cue.preset, 40, true); }
    else if (key == "voice") { bit = 32768; valid = cursor.ReadString(cue.voice, 16, true) &&
        (cue.voice == "marin" || cue.voice == "cedar" || cue.voice == "coral" || cue.voice == "onyx"); }
    if (!valid || !bit || (seen & bit)) return false;
    seen |= bit;
  } while (cursor.Consume(','));
  return cursor.Consume('}') && (seen & (8191 | 32768)) == (8191 | 32768) && cue.validUntilMs > cue.issuedAtMs &&
      cue.validUntilMs - cue.issuedAtMs <= 2000 &&
      (cue.audioPath.empty() || cue.audioPath == "/v1/show/device-cues/" + cue.cueId + "/audio") &&
      (cue.cueType != "LedPreset" || (cue.preset == "wake" || cue.preset == "consider" || cue.preset == "ambient" || cue.preset == "farewell")) &&
      (cue.cueType != "VibratePreset" || cue.preset == "presentation-search-pulse") &&
      (cue.cueType != "PlayLocalClip" || !cue.clipId.empty()) &&
      ((cue.cueType != "LedPreset" && cue.cueType != "VibratePreset" && cue.cueType != "PlayLocalClip") || cue.audioPath.empty());
}
}

bool ParseDeviceShowEnvelope(const std::string& json, DeviceShowEnvelope& envelope) {
  envelope = {};
  if (json.empty() || json.size() > 4096) return false;
  JsonCursor cursor(json.data(), json.size());
  if (!cursor.Consume('{')) return false;
  uint8_t seen = 0;
  do {
    std::string key;
    if (!cursor.ReadString(key, 32, true) || !cursor.Consume(':')) return false;
    uint8_t bit = 0; bool valid = false;
    if (key == "quiet") { bit = 1; valid = cursor.ReadBoolean(envelope.settings.quiet); }
    else if (key == "ambientEnabled") { bit = 2; valid = cursor.ReadBoolean(envelope.settings.ambient); }
    else if (key == "settingsRevision") { bit = 4; valid = Number(cursor, envelope.settings.revision); }
    else if (key == "cue") { bit = 8; valid = cursor.ReadNull() || Cue(cursor, envelope.cue); }
    else if (key == "cancelledCueId") { bit = 16; valid = cursor.ReadNull() || Uuid(cursor, envelope.cancelledCueId); }
    else if (key == "stopRevision") { bit = 32; valid = Number(cursor, envelope.settings.stopRevision); }
    if (!valid || !bit || (seen & bit)) return false;
    seen |= bit;
  } while (cursor.Consume(','));
  return cursor.Consume('}') && cursor.AtEnd() && seen == 63;
}

std::string DeviceShowAckJson(const DeviceShowAck& ack) {
  std::string json = "{\"bootId\":\"" + ack.cue.bootId + "\",\"generation\":" + std::to_string(ack.cue.generation) +
      ",\"deviceRevision\":" + std::to_string(ack.cue.deviceRevision) + ",\"outcome\":";
  AppendJsonString(ack.outcome, json); json += ",\"evidence\":"; AppendJsonString(ack.evidence, json);
  json += ",\"reason\":"; if (ack.reason.empty()) json += "null"; else AppendJsonString(ack.reason, json);
  return json + "}";
}

void DeviceShowMailbox::PublishState(bool allowed, const DeviceShowSettings& applied, uint64_t generation, uint64_t revision) {
  std::lock_guard<std::mutex> lock(mutex_); allowed_ = allowed; applied_ = applied; generation_ = generation; revision_ = revision;
}
bool DeviceShowMailbox::PollState(DeviceShowSettings& applied, uint64_t& generation, uint64_t& revision) const {
  std::lock_guard<std::mutex> lock(mutex_); applied = applied_; generation = generation_; revision = revision_; return allowed_;
}
void DeviceShowMailbox::Settings(const DeviceShowSettings& desired) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (desired.revision > desired_.revision || desired.stopRevision > desired_.stopRevision) { desired_ = desired; desiredPending_ = true; }
}
bool DeviceShowMailbox::TakeSettings(DeviceShowSettings& desired) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (!desiredPending_) return false;
  desired = desired_; desiredPending_ = false; return true;
}
bool DeviceShowMailbox::Offer(const DeviceShowCue& cue, std::shared_ptr<DeviceShowAudio> audio) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (!allowed_ || current_.stage != Stage::Empty || retiredAudio_ || ackCount_ > 2 || cue.cueId.empty()) return false;
  current_ = {}; current_.cue = cue; current_.audio = std::move(audio); current_.stage = Stage::Offered; return true;
}
bool DeviceShowMailbox::IsActive() const { std::lock_guard<std::mutex> lock(mutex_); return current_.stage != Stage::Empty; }
void DeviceShowMailbox::Cancel(const std::string& cueId) {
  std::lock_guard<std::mutex> lock(mutex_);
  cancelledCueId_ = cueId.empty() ? current_.cue.cueId : cueId;
}
bool DeviceShowMailbox::IsCancelled(const std::string& cueId) const {
  std::lock_guard<std::mutex> lock(mutex_); return !cueId.empty() && cueId == cancelledCueId_;
}
std::shared_ptr<DeviceShowAudio> DeviceShowMailbox::TakeRetiredAudio() {
  std::lock_guard<std::mutex> lock(mutex_); return std::move(retiredAudio_);
}
DeviceShowMailbox::View DeviceShowMailbox::Read() const { std::lock_guard<std::mutex> lock(mutex_); return current_; }
bool DeviceShowMailbox::Accept(uint64_t nowMs) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (current_.stage != Stage::Offered || ackCount_ > 2) return false;
  current_.stage = Stage::Accepted; current_.executionDeadlineMs = nowMs + 20000;
  acks_[(ackHead_ + ackCount_++) % acks_.size()] = {current_.cue, "accepted", "commanded", ""};
  return true;
}
bool DeviceShowMailbox::TakeAudio(DeviceShowCue& cue) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (current_.stage != Stage::Accepted || !current_.acceptedAckConfirmed || current_.cue.audioPath.empty() || current_.cue.cueId == cancelledCueId_) return false;
  current_.stage = Stage::Playing; cue = current_.cue; return true;
}
bool DeviceShowMailbox::PlaybackCurrent(const std::string& cueId, uint64_t nowMs) const {
  std::lock_guard<std::mutex> lock(mutex_);
  return current_.stage == Stage::Playing && current_.cue.cueId == cueId && cueId != cancelledCueId_ && nowMs < current_.executionDeadlineMs;
}
void DeviceShowMailbox::AudioFinished(const std::string& cueId, bool succeeded, const char* error) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (current_.cue.cueId != cueId || current_.stage != Stage::Playing) return;
  current_.stage = Stage::AudioFinished; current_.audioSucceeded = succeeded; current_.audioError = error;
}
void DeviceShowMailbox::Finish(const char* outcome, const char* evidence, const char* reason) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (current_.stage == Stage::Empty) return;
  if (ackCount_ < acks_.size()) acks_[(ackHead_ + ackCount_++) % acks_.size()] = {current_.cue, outcome, evidence, reason};
  retiredAudio_ = std::move(current_.audio);
  current_ = {};
}
bool DeviceShowMailbox::ReadAck(DeviceShowAck& ack) const {
  std::lock_guard<std::mutex> lock(mutex_); if (!ackCount_) return false; ack = acks_[ackHead_]; return true;
}
void DeviceShowMailbox::ConfirmAck(const std::string& cueId, const std::string& outcome) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (ackCount_ && acks_[ackHead_].cue.cueId == cueId && acks_[ackHead_].outcome == outcome) {
    if (current_.cue.cueId == cueId && outcome == "accepted") current_.acceptedAckConfirmed = true;
    acks_[ackHead_] = {}; ackHead_ = (ackHead_ + 1) % acks_.size(); --ackCount_;
  }
}

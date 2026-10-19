#include "../src/features/show_cues/BorrowedShowToken.h"
#include "physical_effects_test_support.h"
#include "../src/features/show_cues/DeviceShowRuntime.h"
#include <cstring>
#include <iostream>
#include <vector>

namespace {
const char* Boot = "2c016a96-244f-4164-89de-52bd70a21920";
const char* CueId = "3c016a96-244f-4164-89de-52bd70a21921";
const uint8_t Pcm[] = {0, 0};
const kufr::offline::Clip Clip{"ambient-1", "synthetic", Pcm, 2, 1, "synthetic", 0};
const kufr::offline::BankView Bank{&Clip, 1, 2, 1500000, true, "synthetic", "catalog", "marin"};
struct Audio final : kufr::offline::AudioPort {
  bool busy = false, failed = false, available = true;
  int plays = 0, cancels = 0;
  kufr::offline::ClipFormat format = kufr::offline::ClipFormat::PcmS16Le;
  bool Available() const override { return available; }
  bool Play(const kufr::offline::Clip& clip, uint64_t) override {
    if (busy || !available) return false;
    busy = true; failed = false; format = clip.format; ++plays; return true;
  }
  void Cancel() override { ++cancels; busy = false; failed = true; }
  bool Busy() const override { return busy; }
  bool Failed() const override { return failed; }
};
struct Storage final : kufr::offline::StoragePort {
  kufr::offline::Outbox data;
  bool Read(kufr::offline::Outbox& value) override { value = data; return true; }
  bool Write(const kufr::offline::Outbox& value) override { data = value; return true; }
};
std::shared_ptr<DeviceShowAudio> AudioBuffer() {
  return std::make_shared<DeviceShowAudio>(new uint8_t[DeviceShowAudio::MaximumBytes],
      DeviceShowAudio::MaximumBytes, [](uint8_t* bytes) { delete[] bytes; });
}
std::vector<uint8_t> Wav() {
  std::vector<uint8_t> bytes(46);
  std::memcpy(bytes.data(), "RIFF", 4); bytes[4] = 38;
  std::memcpy(bytes.data() + 8, "WAVEfmt ", 8); bytes[16] = 16;
  bytes[20] = 1; bytes[22] = 1; bytes[24] = 0; bytes[25] = 125;
  bytes[28] = 0; bytes[29] = 250; bytes[32] = 2; bytes[34] = 16;
  std::memcpy(bytes.data() + 36, "data", 4); bytes[40] = 2;
  return bytes;
}
struct Rig {
  Fixture fixture;
  Audio audio;
  Storage storage;
  kufr::offline::OfflineShow offline{fixture.runtime, fixture.state, fixture.control,
      fixture.presentation, fixture.adapter, audio, storage, "device", Boot, Boot, &Bank};
  DeviceShowMailbox mailbox;
  DeviceShowRuntime show{fixture.runtime, fixture.state, fixture.adapter, offline, audio, mailbox, Boot};
  uint64_t now = 30;
  Rig() { show.Tick(now, true); }
  DeviceShowCue Cue() {
    ConversationRuntimeSnapshot snapshot; assert(fixture.state.Read(snapshot));
    DeviceShowCue cue;
    cue.cueId = CueId; cue.duelId = Boot; cue.bootId = Boot; cue.round = 1;
    cue.generation = snapshot.generation; cue.deviceRevision = snapshot.deviceRevision;
    cue.issuedAtMs = now; cue.validUntilMs = now + 2000;
    cue.sceneId = "consider"; cue.compartment = 3; cue.voice = "marin";
    return cue;
  }
  void Tick(bool idle = true, bool audioAvailable = true) { now += 10; fixture.Tick(now); show.Tick(now, idle, audioAvailable); }
  void Ack(const std::string& outcome) {
    DeviceShowAck ack; assert(mailbox.ReadAck(ack)); assert(ack.outcome == outcome);
    mailbox.ConfirmAck(ack.cue.cueId, ack.outcome);
  }
  void DrainLed() { for (unsigned index = 0; index < 30; ++index) Tick(); }
};

void TypedAdaptersAndRealPlaybackBoundary() {
  {
    Rig f; auto cue = f.Cue(); cue.cueType = "LedPreset"; cue.preset = "consider";
    assert(f.mailbox.Offer(cue)); f.Tick(); f.Ack("accepted");
    for (size_t index = 0; index < f.fixture.io.pixels.size(); ++index)
      if (index < 36 || index >= 54) assert(f.fixture.io.pixels[index] == 0);
    assert(f.fixture.io.pixels[36] || f.fixture.io.pixels[37] || f.fixture.io.pixels[38]);
    f.DrainLed(); f.Ack("completed");
    assert(f.audio.plays == 0 && f.fixture.io.servoWrites == 0 && f.fixture.io.vibrationWrites == 0);
  }
  {
    Rig f; auto cue = f.Cue(); cue.cueType = "VibratePreset"; cue.preset = "presentation-search-pulse";
    assert(f.mailbox.Offer(cue)); f.Tick(); f.Ack("accepted");
    assert(f.fixture.io.vibrationWrites == 1 && f.fixture.io.servoWrites == 0);
    f.DrainLed(); f.Ack("completed"); assert(!f.fixture.runtime.HasDecoration());
  }
  {
    Rig f; auto cue = f.Cue(); cue.cueType = "PlayLocalClip"; cue.clipId = "ambient-1";
    assert(f.mailbox.Offer(cue)); f.Tick(); f.Ack("accepted");
    assert(f.audio.plays == 1 && f.fixture.io.lightWrites == 0);
    DeviceShowAck ack; assert(!f.mailbox.ReadAck(ack));
    f.audio.busy = false; f.Tick();
    assert(f.mailbox.ReadAck(ack) && ack.outcome == "completed" && ack.evidence == "playback_finished");
  }
  {
    Rig f; auto cue = f.Cue(); cue.cueType = "PlayLocalClip"; cue.clipId = "not-installed";
    assert(f.mailbox.Offer(cue)); f.Tick();
    DeviceShowAck ack; assert(f.mailbox.ReadAck(ack) && ack.outcome == "rejected" && ack.evidence == "missing_assets");
    assert(!f.fixture.runtime.HasDecoration() && f.audio.plays == 0);
  }
  {
    Rig f; auto cue = f.Cue(); cue.speechText = "Synthetic test only";
    cue.audioPath = "/v1/show/device-cues/" + cue.cueId + "/audio";
    auto audio = AudioBuffer(); assert(f.mailbox.Offer(cue, audio)); f.Tick();
    DeviceShowCue granted; assert(!f.mailbox.TakeAudio(granted)); // Server has not ACKed admission.
    f.Ack("accepted"); assert(f.mailbox.TakeAudio(granted));
    auto wav = Wav(); assert(audio->Append(wav.data(), 17) && audio->Append(wav.data() + 17, wav.size() - 17) && audio->Finish());
    f.mailbox.AudioFinished(cue.cueId, true, ""); f.DrainLed();
    assert(f.audio.busy && f.audio.plays == 1 && f.audio.format == kufr::offline::ClipFormat::PcmS16Le32k);
    DeviceShowAck ack; assert(!f.mailbox.ReadAck(ack));
    f.audio.busy = false; f.Tick(); assert(f.mailbox.ReadAck(ack) && ack.evidence == "playback_finished");
    assert(f.fixture.journal.begins == 0 && f.fixture.io.servoWrites == 0 && f.fixture.io.vibrationWrites == 0);
  }
}

void CancellationEpochSettingsAndBounds() {
  {
    Rig f; auto cue = f.Cue(); cue.audioPath = "/v1/show/device-cues/" + cue.cueId + "/audio";
    auto audio = AudioBuffer(); assert(f.mailbox.Offer(cue, audio)); f.Tick(); f.Ack("accepted");
    DeviceShowCue granted; assert(f.mailbox.TakeAudio(granted));
    auto wav = Wav(); assert(audio->Append(wav.data(), wav.size()) && audio->Finish());
    f.mailbox.AudioFinished(cue.cueId, true, ""); f.DrainLed(); assert(f.audio.busy);
    f.mailbox.Cancel(cue.cueId); f.Tick(); assert(!f.audio.busy); f.Ack("cancelled");
    f.mailbox.AudioFinished(cue.cueId, true, "late"); f.Tick();
    DeviceShowAck ack; assert(!f.mailbox.ReadAck(ack));
  }
  {
    Rig f; auto cue = f.Cue(); ++cue.generation;
    assert(f.mailbox.Offer(cue)); f.Tick(); f.Ack("rejected"); assert(f.fixture.io.lightWrites == 0);
  }
  {
    Rig f;
    f.mailbox.Settings({5, false, true}); f.Tick(); assert(f.offline.AmbientEnabled());
    f.offline.Stop(); assert(!f.offline.AmbientEnabled());
    assert(f.fixture.runtime.RecoverAfterInspection());
    f.mailbox.Settings({5, false, true}); f.Tick(); assert(!f.offline.AmbientEnabled());
    f.mailbox.Settings({6, false, true}); f.Tick(); assert(f.offline.AmbientEnabled());
  }
  {
    Rig f; auto cue = f.Cue(); cue.cueType = "VibratePreset"; cue.preset = "presentation-search-pulse";
    assert(f.mailbox.Offer(cue)); f.Tick(); f.Ack("accepted");
    f.mailbox.Settings({8, false, true, 2}); f.Tick();
    assert(f.show.TakeRemoteStop() && !f.show.TakeRemoteStop());
    assert(f.fixture.control.NeedsRecovery() && !f.offline.AmbientEnabled());
    assert(f.offline.ServerStopRevision() == 2 && f.storage.data.serverStopRevision == 2);
    f.Ack("cancelled");
    f.mailbox.Settings({8, false, true, 2}); f.Tick(); assert(!f.show.TakeRemoteStop());
    assert(f.fixture.runtime.RecoverAfterInspection());
    f.mailbox.Settings({9, false, true, 2}); f.Tick();
    assert(!f.show.TakeRemoteStop() && f.offline.AmbientEnabled());
  }
  {
    Rig f; auto cue = f.Cue(); cue.audioPath = "/v1/show/device-cues/" + cue.cueId + "/audio";
    cue.voice = "coral";
    assert(f.mailbox.Offer(cue, AudioBuffer())); f.Tick(); f.Ack("rejected");
    assert(f.audio.plays == 0 && f.fixture.io.lightWrites == 0);
  }
  auto audio = AudioBuffer(); auto wav = Wav(); wav[22] = 2;
  assert(audio->Append(wav.data(), wav.size()) && !audio->Finish());
  audio->Reset(); std::vector<uint8_t> oversized(DeviceShowAudio::MaximumBytes + 1);
  assert(!audio->Append(oversized.data(), oversized.size()));
}

void LivePhysicalCuesAndBorrowedCredentials() {
  for (const auto* type : {"LedPreset", "VibratePreset"}) {
    Rig f; auto cue = f.Cue(); cue.cueType = type;
    cue.preset = cue.cueType == "LedPreset" ? "consider" : "presentation-search-pulse";
    ConversationRuntimeSnapshot live; assert(f.fixture.state.Read(live));
    assert(f.mailbox.Offer(cue)); f.Tick(true, false); f.Ack("accepted");
    assert(f.fixture.state.Matches(live) && f.audio.plays == 0 && f.audio.cancels == 0);
    for (unsigned index = 0; index < 30; ++index) f.Tick(true, false);
    f.Ack("completed"); assert(f.fixture.state.Matches(live));
    assert(f.fixture.io.servoWrites == 0 && !f.fixture.runtime.HasDecoration());
  }
  for (const auto* type : {"PlayLocalClip", "NarratedBeat"}) {
    Rig f; auto cue = f.Cue(); cue.cueType = type; cue.clipId = "ambient-1";
    if (cue.cueType == "NarratedBeat") cue.audioPath = "/v1/show/device-cues/" + cue.cueId + "/audio";
    assert(f.mailbox.Offer(cue, AudioBuffer())); f.Tick(true, false);
    DeviceShowAck ack; assert(f.mailbox.ReadAck(ack) && ack.outcome == "rejected" && ack.reason == "audio_busy");
    assert(f.audio.plays == 0 && f.audio.cancels == 0 && f.fixture.io.lightWrites == 0);
  }
  BorrowedShowToken token;
  BorrowedShowToken::Copy copy;
  assert(!token.Read(0, copy));
  token.Publish("existing-owner-access-token", 100);
  assert(token.Read(99, copy) && std::string(copy.bytes.data()) == "existing-owner-access-token");
  assert(!token.Read(100, copy));
  token.Clear(); assert(!token.Read(1, copy));
  token.Publish(std::string(257, 'x').c_str(), 100); assert(!token.Read(1, copy));
}

void StrictWireAndSafeMetadata() {
  const std::string base = "{\"quiet\":false,\"ambientEnabled\":true,\"settingsRevision\":3,\"stopRevision\":0,\"cancelledCueId\":null,\"cue\":";
  const std::string cue = "{\"cueId\":\"" + std::string(CueId) + "\",\"duelId\":null,\"round\":1,\"bootId\":\"" + Boot +
      "\",\"generation\":0,\"deviceRevision\":1,\"issuedAtDeviceMs\":30,\"validUntilDeviceMs\":2030,\"sceneId\":\"consider\",\"compartment\":3,\"speechText\":\"test\",\"audioPath\":null,\"clipId\":null,\"voice\":\"marin\"}";
  DeviceShowEnvelope parsed;
  assert(ParseDeviceShowEnvelope(base + "null}", parsed));
  assert(ParseDeviceShowEnvelope(base + cue + "}", parsed) && parsed.cue.compartment == 3);
  assert(!ParseDeviceShowEnvelope(base + cue + ",\"servo\":2}", parsed));
  assert(!ParseDeviceShowEnvelope(base + "null,\"quiet\":true}", parsed));
  auto wrongPath = cue; const auto at = wrongPath.find("\"audioPath\":null");
  wrongPath.replace(at, 16, "\"audioPath\":\"https://untrusted/audio\"");
  assert(!ParseDeviceShowEnvelope(base + wrongPath + "}", parsed));
}
}
int main() {
  TypedAdaptersAndRealPlaybackBoundary(); CancellationEpochSettingsAndBounds();
  LivePhysicalCuesAndBorrowedCredentials(); StrictWireAndSafeMetadata();
  std::cout << "Device cue codec, typed adapters, duel playback drain, cancellation, settings and bounds passed\n";
}

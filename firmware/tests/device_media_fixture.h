#pragma once
#include "../src/features/voice_turn/DeviceMediaSession.h"
#include <cassert>
#include <algorithm>
#include <cstring>
#include <deque>
#include <iostream>
#include <memory>

namespace {
const std::string Id = "12345678-1234-4234-8234-123456789abc";
const std::string Other = "12345678-1234-4234-8234-123456789abd";
struct Socket final : DeviceMediaSocketPort {
  struct Frame { bool binary; std::vector<uint8_t> bytes; };
  std::deque<Frame> incoming;
  std::vector<Frame> sent;
  bool failSend = false, closed = false;
  bool retainSent = true, automaticAck = false;
  uint64_t* now = nullptr;
  uint64_t holdUntil = 0;
  size_t sentCount = 0, highWater = 0;
  bool Open(const std::string&, const std::string&, const std::string&, const std::string&) override { return true; }
  bool Send(bool binary, const uint8_t* data, size_t size) override {
    ++sentCount;
    if (retainSent) sent.push_back({binary, {data, data + size}});
    if (automaticAck && !failSend) {
      if (binary) {
        assert(size >= 16);
        auto read = [&](size_t at) { return static_cast<uint32_t>(data[at]) | (static_cast<uint32_t>(data[at + 1]) << 8) |
            (static_cast<uint32_t>(data[at + 2]) << 16) | (static_cast<uint32_t>(data[at + 3]) << 24); };
        Json("{\"type\":\"media.ack\",\"stream\":" + std::to_string(read(4)) + ",\"sequence\":" + std::to_string(read(8)) + "}");
      } else {
        const std::string wire(reinterpret_cast<const char*>(data), size);
        const std::string key = "\"requestId\":\"";
        const auto at = wire.find(key);
        if (at != std::string::npos) Json("{\"type\":\"control.ack\",\"requestId\":\"" + wire.substr(at + key.size(), 36) + "\",\"status\":204}");
      }
    }
    return !failSend;
  }
  ReceiveResult Receive(uint8_t* data, size_t capacity, size_t& used, bool& binary) override {
    used = 0;
    if (now && *now < holdUntil) return ReceiveResult::Idle;
    if (incoming.empty()) return ReceiveResult::Idle;
    auto next = std::move(incoming.front()); incoming.pop_front(); assert(next.bytes.size() <= capacity);
    used = next.bytes.size(); binary = next.binary; std::memcpy(data, next.bytes.data(), used);
    return ReceiveResult::Message;
  }
  void Close() override { closed = true; }
  const char* Error() const override { return "fixture"; }
  void Json(const std::string& text) {
    incoming.push_back({false, {text.begin(), text.end()}});
    highWater = std::max(highWater, incoming.size());
  }
};
struct Audio final : VoiceAudioPort {
  bool Available() const override { return true; }
  void Listen(bool) override { assert(false); }
  bool TakeInput(std::vector<uint8_t>&) override { assert(false); return false; }
  bool Play(const std::vector<uint8_t>&) override { assert(false); return false; }
  bool Drain() override { assert(false); return false; }
  bool CancelPlayback() override { assert(false); return false; }
  bool Failed() const override { return false; }
  void Stop() override { assert(false); }
};
struct Playback final : VoicePlaybackPort {
  size_t bytes = 0;
  DrainHandle next = 0;
  bool ready = false;
  bool stopped = false;
  bool Queue(uint64_t, const uint8_t*, size_t length) override { bytes += length; return true; }
  Progress Drain(uint64_t) override { return Progress::Succeeded; }
  bool RequestDrain(uint64_t, DrainHandle& handle) override { handle = ++next; return true; }
  Progress PollDrain(DrainHandle) override { return ready ? Progress::Succeeded : Progress::Pending; }
  void AbandonDrain(DrainHandle) override {}
  Progress Cancel(uint64_t) override { return Progress::Succeeded; }
  bool Failed() const override { return false; }
  void Stop() override { stopped = true; }
};
struct Fixture {
  Socket socket;
  Audio audio;
  Playback playback;
  uint64_t now = 100;
  unsigned completed = 0;
  bool modelCapture = false;
  size_t captureFrames = 0;
  std::string nextId = Id;
  std::array<uint8_t, 65536> workspace{};
  std::vector<uint8_t> capture;
  TurnCorrelation opening;
  ConversationRuntimeSnapshot snapshot;
  std::unique_ptr<DeviceMediaSession> session;
  std::unique_ptr<RealtimeSessionEngine> engine;
  explicit Fixture(bool live = true, bool initialSession = true, VoicePlaybackPort* external = nullptr, uint64_t start = 100) {
    now = start;
    opening.deviceId = "device"; opening.conversationId = Id; opening.bootId = Id; opening.clientTurnId = Id;
    opening.generation = snapshot.generation = 1; opening.deviceRevision = snapshot.deviceRevision = 1;
    opening.operationEpoch = snapshot.operationEpoch = UINT64_MAX; snapshot.readyMask = 63; snapshot.catalogVersion = "catalog";
    session.reset(new DeviceMediaSession(socket, [&] { return now; }, [&] { return nextId; },
        [&](std::vector<uint8_t>& pcm) {
          if (modelCapture) {
            const size_t count = std::min<size_t>(5, captureFrames);
            pcm.assign(count * 960, 0); captureFrames -= count; return true;
          }
          pcm = std::move(capture); capture.clear(); return true;
        }, workspace.data(), workspace.size()));
    engine.reset(new RealtimeSessionEngine(audio, live, [] { return true; }, [&] { return now; },
        [] { return 1800000000ULL; }, [](const char*) {},
        [](const std::string&, const void*, size_t, const char*, const std::string&, unsigned) { assert(false); return 0; },
        opening, snapshot, [&](const TurnOperationObservation&) { ++completed; return true; },
        {external ? *external : playback, *session, [](bool) {}}));
    session->Attach(*engine);
    assert(session->Open("example.invalid", opening, "token", Id, live, "{}"));
    if (initialSession) {
      RealtimeEvent event; event.type = "session"; event.streamId = Id; assert(engine->Event(event));
    }
  }
  ~Fixture() { session.reset(); engine.reset(); }
  void Input() {
    socket.Json("{\"type\":\"media.input\",\"stream\":1,\"inputId\":\"" + Id +
        "\",\"format\":\"pcm16-mono-24000\",\"maxFrameBytes\":4096,\"credit\":1}");
    assert(session->Step());
    socket.Json("{\"type\":\"listening\",\"inputId\":\"" + Id + "\",\"responseGeneration\":0}");
    assert(session->Step() && engine->Uploading());
  }
  VoiceControlPort::Handle Checkpoint() {
    VoiceControlPort::Command command; command.kind = VoiceControlPort::Kind::Checkpoint;
    command.playbackId = Id; command.pcmBytes = 6;
    VoiceControlPort::Handle handle = 0;
    assert(session->Submit(command, handle) && handle); return handle;
  }
};
inline std::string Correlation() {
  return "{\"deviceId\":\"device\",\"bootId\":\"" + Id + "\",\"conversationId\":\"" + Id +
      "\",\"clientTurnId\":\"" + Id + "\",\"generation\":1,\"deviceRevision\":1,\"operationEpoch\":\"18446744073709551615\"}";
}
inline void Json(Fixture& value, const std::string& wire) { value.socket.Json(wire); assert(value.session->Step()); }
inline void StartWire(Fixture& value) {
  Json(value, "{\"type\":\"session\",\"streamId\":\"" + Id + "\",\"audioFormat\":\"pcm16-mono-24000\"}");
  Json(value, "{\"type\":\"media.operation\",\"operationId\":\"" + Id + "\",\"responseGeneration\":0}");
  Json(value, "{\"type\":\"started\",\"operationId\":\"" + Id + "\",\"correlation\":" + Correlation() + ",\"responseGeneration\":0}");
}
inline void AudioWire(Fixture& value, uint32_t stream, uint64_t generation) {
  Json(value, "{\"type\":\"media.output\",\"stream\":" + std::to_string(stream) +
      ",\"responseGeneration\":" + std::to_string(generation) + ",\"format\":\"pcm16-mono-24000\"}");
  const uint8_t pcm[] = {1, 2, 3, 4}; uint8_t frame[20]; size_t used;
  assert(kufr_media::WriteInputPcm(stream, 0, pcm, sizeof(pcm), frame, sizeof(frame), used)); frame[3] = 2;
  value.socket.incoming.push_back({true, {frame, frame + used}}); assert(value.session->Step());
}
inline void CompletedWire(Fixture& value, uint64_t generation) {
  Json(value, "{\"type\":\"completed\",\"operationId\":\"" + Id + "\",\"correlation\":" + Correlation() +
      ",\"result\":{\"reply\":\"fixture\",\"decision\":\"no_selection\",\"reason\":\"fixture\",\"proposal\":null},\"costMicroUsd\":0,\"responseGeneration\":" +
      std::to_string(generation) + "}");
}

}

#include "EspIdfDeviceMediaTransport.h"
#include "EspIdfDeviceMediaSocket.h"
#include "DeviceMediaSession.h"
#include "BoundedVoicePlayback.h"
#include "PlaybackAuthorityPort.h"
#include "../conversation/EspIdfConversationUuid.h"
#include "esp_heap_caps.h"
#include "esp_timer.h"
#include "freertos/task.h"
#include <atomic>
#include <ctime>
#include <memory>
#include <new>

namespace {
uint64_t Now() { return static_cast<uint64_t>(esp_timer_get_time() / 1000); }
struct Workspace {
  uint8_t* pcm = static_cast<uint8_t*>(heap_caps_malloc(BoundedPlaybackOwner::WorkspaceBytes, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT));
  uint8_t* rx = static_cast<uint8_t*>(heap_caps_malloc(65536, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT));
  ~Workspace() {
    if (pcm) { std::fill(pcm, pcm + BoundedPlaybackOwner::WorkspaceBytes, 0); heap_caps_free(pcm); }
    if (rx) { std::fill(rx, rx + 65536, 0); heap_caps_free(rx); }
  }
};
struct PlaybackWorker {
  explicit PlaybackWorker(BoundedPlaybackOwner& value) : owner(value) {}
  BoundedPlaybackOwner& owner;
  std::atomic<bool> stop{false}, done{false};
  static void Run(void* argument) {
    auto& worker = *static_cast<PlaybackWorker*>(argument);
    while (!worker.stop.load()) {
      if (!worker.owner.RunOne()) vTaskDelay(pdMS_TO_TICKS(1));
    }
    worker.done.store(true);
    vTaskDelete(nullptr);
  }
};
uint64_t NextSession() {
  static std::atomic<uint64_t> next{1};
  auto candidate = next.load();
  while (candidate != UINT64_MAX)
    if (next.compare_exchange_weak(candidate, candidate + 1)) return candidate;
  return 0;
}
}
EspIdfDeviceMediaTransport::EspIdfDeviceMediaTransport(const std::string& host, const std::string& token,
    EspIdfVoiceAudio& audio, std::function<bool()> current, std::function<void(const char*)> phase, bool live,
    std::function<void(const VoiceMediaHealth&)> health)
    : host_(host), token_(token), audio_(audio), current_(std::move(current)), phase_(std::move(phase)), live_(live),
      health_(std::move(health)) {}
EspIdfDeviceMediaTransport::~EspIdfDeviceMediaTransport() {
  std::fill(token_.begin(), token_.end(), '\0');
}
bool EspIdfDeviceMediaTransport::Run(const std::string& body, const std::string& key,
    const TurnCorrelation& opening, const ConversationRuntimeSnapshot& snapshot,
    const std::function<bool(const TurnOperationObservation&)>& completed) {
  if (!audio_.Available() || !current_()) { error_ = "audio_unavailable"; return false; }
  const auto sessionId = NextSession();
  Workspace memory;
  if (!sessionId || !memory.pcm || !memory.rx) { error_ = "media_capacity_unavailable"; return false; }
  PlaybackAuthorityPort guarded(audio_, current_);
  BoundedPlaybackOwner owner(guarded, memory.pcm, BoundedPlaybackOwner::WorkspaceBytes,
      [](void* context) { static_cast<EspIdfVoiceAudio*>(context)->Stop(); },
      [](void* context) { return static_cast<EspIdfVoiceAudio*>(context)->MutePlayback(); }, &audio_);
  BoundedVoicePlayback playback(owner, sessionId);
  VoiceMediaHealth health;
  health.session = sessionId;
  health.termination = 1;
  const auto publish = [&]() {
    const auto playbackState = owner.Inspect();
    health.playbackQueuedBytes = static_cast<uint32_t>(playbackState.queuedBytes);
    health.playbackHighWaterBytes = std::max(health.playbackHighWaterBytes, health.playbackQueuedBytes);
    health.playedPcmBytes = playbackState.playedSamples * 2;
    health.captureQueuedBytes = audio_.CaptureQueuedBytes();
    health.captureHighWaterBytes = audio_.CaptureHighWaterBytes();
    health.captureOverruns = audio_.CaptureOverruns();
    if (health_) health_(health);
  };
  PlaybackWorker worker(owner);
  TaskHandle_t task = nullptr;
  if (xTaskCreate(PlaybackWorker::Run, "voice_playback", 12288, &worker, 3, &task) != pdPASS) {
    error_ = "media_capacity_unavailable"; return false;
  }
  bool success = false;
  const auto beginAt = Now();
  auto started = playback.Begin();
  while (started == VoicePlaybackPort::Progress::Pending && current_() && Now() - beginAt < 2000) {
    vTaskDelay(pdMS_TO_TICKS(1)); started = playback.Begin();
  }
  if (started == VoicePlaybackPort::Progress::Succeeded && current_()) {
    // Keep large protocol/engine state off the backend task's 16 KiB stack.
    std::unique_ptr<EspIdfDeviceMediaSocket> socket(new (std::nothrow) EspIdfDeviceMediaSocket);
    if (socket) {
      std::unique_ptr<DeviceMediaSession> channel(new (std::nothrow) DeviceMediaSession(*socket, Now,
          GenerateEspIdfConversationUuid, [&](std::vector<uint8_t>& pcm) { return audio_.TakeInput(sessionId, pcm); },
          memory.rx, 65536));
      if (channel) {
        std::unique_ptr<RealtimeSessionEngine> engine(new (std::nothrow) RealtimeSessionEngine(audio_, live_, current_, Now,
            [] { const auto utc = std::time(nullptr); return utc > 0 ? static_cast<uint64_t>(utc) : 0; }, phase_,
            [](const std::string&, const void*, size_t, const char*, const std::string&, unsigned) { return 0; },
            opening, snapshot, completed, {playback, *channel,
                [&](bool enabled) { (void)audio_.SetCaptureEnabled(sessionId, enabled); }}));
        if (engine) {
          channel->Attach(*engine);
          bool valid = channel->Open(host_, opening, token_, key, live_, body);
          while (valid && current_() && (!engine->Closed() || engine->HasPendingActions())) {
            valid = channel->Step();
            publish();
          }
          success = valid && current_() && engine->Closed() && !engine->Failed() && !audio_.Failed();
          if (!success) error_ = *channel->Error() ? channel->Error() : "media_session_failed";
          engine->Stop();
        }
      }
    }
  }
  if (!success && error_.empty()) error_ = "media_capacity_unavailable";
  health.termination = success ? 2 : current_() ? 4 : 3;
  publish();
  (void)audio_.SetCaptureEnabled(sessionId, false);
  playback.Stop(); // Immediate output revocation precedes every borrower join.
  worker.stop.store(true);
  while (!worker.done.load()) vTaskDelay(pdMS_TO_TICKS(1));
  return success;
}

#include "EspIdfDancePlayer.h"
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <sys/stat.h>
#include "esp_random.h"
#include "esp_timer.h"

namespace kufr { namespace dance {
namespace {
class Lock final {
 public:
  explicit Lock(SemaphoreHandle_t mutex) : mutex_(mutex) { xSemaphoreTake(mutex_, portMAX_DELAY); }
  ~Lock() { xSemaphoreGive(mutex_); }
 private:
  SemaphoreHandle_t mutex_;
};
uint64_t NowMs() { return static_cast<uint64_t>(esp_timer_get_time() / 1000); }
#if defined(KUFR_DANCE_HOST_TEST)
constexpr const char* DanceMountPath = "dance-test-assets/";
#else
constexpr const char* DanceMountPath = "/sd-assets/";
#endif
class File final {
 public:
  ~File() { Close(); }
  bool Open(const std::string& filename, uint64_t& bytes) {
    Close();
    const std::string path = std::string(DanceMountPath) + filename;
    file_ = std::fopen(path.c_str(), "rb");
    if (!file_) return false;
    if (fstat(fileno(file_), &original_) != 0 || !S_ISREG(original_.st_mode) || original_.st_size < 0) {
      Close(); return false;
    }
    bytes = static_cast<uint64_t>(original_.st_size); return true;
  }
  bool Read(uint8_t* bytes, size_t count) { return std::fread(bytes, 1, count, file_) == count && !std::ferror(file_); }
  bool Unchanged() const {
    struct stat current{};
    return fstat(fileno(file_), &current) == 0 && S_ISREG(current.st_mode) &&
        current.st_size == original_.st_size && current.st_mtime == original_.st_mtime;
  }
  void Close() { if (file_) { std::fclose(file_); file_ = nullptr; } }
 private:
  FILE* file_ = nullptr;
  struct stat original_{};
};
bool ValidRequest(const std::string& request) {
  if (request.empty() || request.size() > 64) return false;
  for (char ch : request)
    if (!((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') ||
          (ch >= '0' && ch <= '9') || ch == '_' || ch == '-')) return false;
  return true;
}
}
bool EspIdfDancePlayer::Begin() {
  if (mutex_) return false;
  mutex_ = xSemaphoreCreateMutex();
  if (!mutex_) return false;
  if (xTaskCreate(WorkerEntry, "kufr-dance", 6144, this, 3, &task_) != pdPASS) {
    status_.error = "dance_worker_unavailable"; return false;
  }
  status_.phase = "idle"; return true;
}
DanceStatus EspIdfDancePlayer::Status() const {
  if (!mutex_) { DanceStatus status; status.error = "dance_worker_unavailable"; return status; }
  Lock lock(mutex_);
  DanceStatus status = status_;
  status.available = task_ && audio_.Available() && assets_.Status().state == "mounted";
  if (!IsDanceActive(status) && (status.phase == "idle" || status.phase == "unavailable")) {
    status.phase = status.available ? "idle" : "unavailable";
    status.error = status.available ? "" : !task_ ? "dance_worker_unavailable" :
        !audio_.Available() ? "dance_audio_unavailable" : "dance_sd_unavailable";
  }
  return status;
}
bool EspIdfDancePlayer::Start(const std::string& requestId) {
  if (!mutex_ || !task_ || !ValidRequest(requestId)) return false;
  Lock lock(mutex_);
  for (size_t i = 0; i < acceptedCount_; ++i) if (acceptedRequests_[i] == requestId) return false;
  if (IsDanceActive(status_)) return false;
  if (acceptedCount_ == acceptedRequests_.size()) {
    status_.phase = "failed"; status_.error = "dance_request_history_full"; return false;
  }
  if (!audio_.Available()) { status_.phase = "unavailable"; status_.error = "dance_audio_unavailable"; return false; }
  if (!assets_.TryReservePlayback()) { status_.phase = "unavailable"; status_.error = "dance_sd_unavailable"; return false; }
  acceptedRequests_[acceptedCount_++] = requestId;
  status_ = {}; status_.phase = "starting";
  xTaskNotifyGive(task_); return true;
}
void EspIdfDancePlayer::Stop() {
  if (!mutex_) return;
  Lock lock(mutex_);
  if (!IsDanceActive(status_)) return;
  cancellationEpoch_.fetch_add(1);
  status_.phase = "stopping";
  // Stop closes the audio gate without waiting for worker file I/O. Holding the
  // status lock ensures cleanup cannot expose idle before this gate is closed.
  audio_.Stop();
}
void EspIdfDancePlayer::WorkerEntry(void* owner) { static_cast<EspIdfDancePlayer*>(owner)->Worker(); }
void EspIdfDancePlayer::Worker() {
  for (;;) {
    ulTaskNotifyTake(pdTRUE, portMAX_DELAY);
    uint32_t epoch;
    { Lock lock(mutex_);
      // Cancellation before worker wake must not adopt the new cancellation epoch.
      epoch = cancellationEpoch_.load();
      if (status_.phase == "stopping") { assets_.ReleasePlayback(); status_.phase = "idle"; continue; }
    }
    Execute(epoch);
  }
}
bool EspIdfDancePlayer::LoadManifest(DanceManifest& manifest, uint32_t epoch, std::string& error) {
  File config;
  uint64_t size = 0;
  if (!config.Open("dance.csv", size)) { error = "dance_manifest_unavailable"; return false; }
  if (!size || size > MaximumDanceManifestBytes) { error = "dance_manifest_invalid"; return false; }
  std::string text(static_cast<size_t>(size), '\0');
  if (!config.Read(reinterpret_cast<uint8_t*>(&text[0]), text.size()) || !config.Unchanged()) {
    error = "dance_manifest_invalid"; return false;
  }
  config.Close();
  if (!ParseDanceManifest(text, manifest, error)) { error = "dance_manifest_invalid"; return false; }
  for (size_t i = 0; i < manifest.count; ++i) {
    if (Cancelled(epoch)) return false;
    File song;
    std::array<uint8_t, 44> header{};
    uint64_t bytes = 0;
    if (!song.Open(manifest.tracks[i].filename, bytes) || !song.Read(header.data(), header.size()) ||
        !song.Unchanged() || !ParseDanceWav(header.data(), header.size(), bytes, manifest.tracks[i], error)) {
      error = "dance_track_invalid"; return false;
    }
    vTaskDelay(1);
  }
  return !Cancelled(epoch);
}
bool EspIdfDancePlayer::PlayTrack(const DanceTrack& track, uint32_t epoch, std::string& error) {
  File song;
  uint64_t bytes = 0;
  std::array<uint8_t, 44> header{};
  DanceTrack current = track;
  if (!song.Open(track.filename, bytes) || !song.Read(header.data(), header.size()) ||
      !ParseDanceWav(header.data(), header.size(), bytes, current, error) || current.dataBytes != track.dataBytes) {
    error = "dance_track_invalid"; return false;
  }
  if (Cancelled(epoch)) return false;
  audio_.Begin();
  // Begin resets the underlying sticky audio gate. A Stop that raced Begin
  // must be reapplied before Listen or any PCM submission can occur.
  if (Cancelled(epoch)) { audio_.Stop(); return false; }
  audio_.Listen(false);
  if (Cancelled(epoch)) { audio_.Stop(); return false; }
  if (audio_.Failed()) { error = "dance_audio_failed"; return false; }
  DanceAudioTimeline timeline;
  bool first = true;
  std::vector<uint8_t> block;
  block.reserve(DanceBlockBytes);
  uint32_t remaining = track.dataBytes;
  while (remaining && !Cancelled(epoch)) {
    block.resize(std::min<size_t>(DanceBlockBytes, remaining));
    const uint64_t readAt = NowMs();
    if (!song.Read(block.data(), block.size()) || NowMs() - readAt > 100) { error = "dance_sd_read_failed"; return false; }
    if (Cancelled(epoch)) return false;
    const uint64_t playAt = NowMs();
    if (first) { timeline.Start(playAt); first = false; }
    if (timeline.Underrun(playAt)) { error = "dance_audio_underrun"; return false; }
    if (!audio_.Play(block) || audio_.Failed()) { if (!Cancelled(epoch)) error = "dance_audio_failed"; return false; }
    timeline.Submitted(block.size()); remaining -= static_cast<uint32_t>(block.size());
    const uint64_t now = NowMs();
    if (now - playAt > 100 || timeline.Underrun(now)) { error = "dance_audio_underrun"; return false; }
    {
      Lock lock(mutex_);
      if (Cancelled(epoch)) return false;
      status_.phase = "playing"; status_.positionMs = timeline.Position(now);
    }
    vTaskDelay(1);
  }
  if (Cancelled(epoch)) return false;
  if (!song.Unchanged()) { error = "dance_track_invalid"; return false; }
  const uint64_t drainAt = NowMs();
  if (!audio_.Drain() || audio_.Failed()) { if (!Cancelled(epoch)) error = "dance_audio_failed"; return false; }
  if (NowMs() - drainAt > 1000) { error = "dance_audio_underrun"; return false; }
  return !Cancelled(epoch);
}
void EspIdfDancePlayer::Execute(uint32_t epoch) {
  std::string error;
  DanceManifest manifest;
  if (Cancelled(epoch)) { Finish(epoch, error); return; }
  if (LoadManifest(manifest, epoch, error)) {
    size_t previous = manifest.count;
    for (size_t i = 0; i < manifest.count; ++i) if (manifest.tracks[i].filename == previousTrack_) previous = i;
    const size_t selected = SelectDanceTrack(manifest.count, previous, esp_random());
    {
      Lock lock(mutex_);
      if (!Cancelled(epoch)) { DescribeDanceTrack(manifest.tracks[selected], status_); previousTrack_ = status_.track; }
    }
    if (!Cancelled(epoch)) PlayTrack(manifest.tracks[selected], epoch, error);
  }
  Finish(epoch, error);
}
void EspIdfDancePlayer::Finish(uint32_t epoch, const std::string& error) {
  audio_.Stop();
  // Every local FILE has closed before releasing the card. New work remains
  // excluded by the active phase until all cleanup, including Stop, is complete.
  assets_.ReleasePlayback();
  Lock lock(mutex_);
  const bool cancelled = Cancelled(epoch);
  status_.phase = cancelled ? "idle" : error.empty() ? "completed" : "failed";
  status_.error = cancelled ? "" : error;
  if (!cancelled && error.empty()) status_.positionMs = status_.durationMs;
}
} }

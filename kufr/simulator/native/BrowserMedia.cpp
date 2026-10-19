#include "BrowserMedia.h"
#include "conversation/ConversationStartCodec.h"
#include <algorithm>
#include <chrono>
#include <ctime>
#include <iomanip>
#include <random>
#include <sstream>

namespace {
void Wipe(std::vector<uint8_t>& bytes) { std::fill(bytes.begin(), bytes.end(), 0); bytes.clear(); }
std::string Json(const std::string& text) {
  std::string result = "\"";
  const char* hex = "0123456789abcdef";
  for (unsigned char byte : text) {
    if (byte == '"' || byte == '\\') { result += '\\'; result += static_cast<char>(byte); }
    else if (byte < 32) { result += "\\u00"; result += hex[byte >> 4]; result += hex[byte & 15]; }
    else result += static_cast<char>(byte);
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
class BrowserCamera final : public CostumeCameraPort {
 public:
  BrowserCamera(BrowserMedia& owner, bool available) : owner_(owner), available_(available) {}
  bool Available() const override { return available_; }
  bool Capture(std::vector<uint8_t>& jpeg) override { return owner_.Capture(jpeg); }
 private:
  BrowserMedia& owner_;
  bool available_;
};
uint64_t Utc() { const auto utc = std::time(nullptr); return utc > 0 ? static_cast<uint64_t>(utc) : 0; }
}
uint64_t MediaWallMs() {
  return static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::milliseconds>(
      std::chrono::steady_clock::now().time_since_epoch()).count());
}
std::string MediaUuid() {
  std::random_device entropy;
  uint8_t bytes[16];
  for (auto& byte : bytes) byte = static_cast<uint8_t>(entropy());
  bytes[6] = static_cast<uint8_t>((bytes[6] & 15) | 64);
  bytes[8] = static_cast<uint8_t>((bytes[8] & 63) | 128);
  const char* hex = "0123456789abcdef";
  std::string value;
  for (size_t index = 0; index < 16; ++index) {
    if (index == 4 || index == 6 || index == 8 || index == 10) value += '-';
    value += hex[bytes[index] >> 4]; value += hex[bytes[index] & 15];
  }
  return value;
}
BrowserMedia::BrowserMedia(const std::string& device, const std::string& boot, ConversationRuntimeState& runtime)
    : device_(device), boot_(boot), runtime_(runtime) {}
BrowserMedia::~BrowserMedia() { Cancel(); if (worker_.joinable()) worker_.join(); }
bool BrowserMedia::Current() const { return !cancelled_.load() && !failed_.load() && runtime_.Matches(snapshot_); }
void BrowserMedia::ClearLocked() {
  for (auto& output : output_) Wipe(output.bytes);
  for (auto& input : input_) Wipe(input);
  for (auto& event : events_) Wipe(event);
  for (auto& request : requests_) Wipe(request.second->bytes);
  output_.clear(); input_.clear(); events_.clear(); requests_.clear(); Wipe(jpeg_);
  inputBytes_ = eventBytes_ = 0; completion_.reset();
}
bool BrowserMedia::Start(const std::string& session, const std::string& requestId, bool gptLive,
    const std::string& voice, const std::string& speed, bool imageConsent, bool cameraAvailable,
    const ConversationRuntimeSnapshot& snapshot) {
  { std::lock_guard<std::mutex> lock(mutex_); if (!done_ || busy_.load() || session == session_ || requestId == lastRequestId_) return false; }
  if (worker_.joinable()) worker_.join();
  VoiceWorkflowRequest request;
  request.requestId = requestId; request.voice = voice; request.speed = speed;
  request.imageConsent = imageConsent; request.snapshot = snapshot;
  request.correlation.deviceId = device_; request.correlation.bootId = boot_;
  request.correlation.generation = snapshot.generation; request.correlation.deviceRevision = snapshot.deviceRevision;
  {
    std::lock_guard<std::mutex> lock(mutex_);
    ClearLocked(); session_ = session; snapshot_ = snapshot; lastRequestId_ = requestId;
    cameraAvailable_ = cameraAvailable; photoWarning_.clear(); ++epoch_; ++outputEpoch_;
    if (!audioObservation_.empty()) audioObservation_ = "browser-playback-unknown";
    playbackPending_ = false;
    inputEpoch_ = inputSequence_ = jpegSequence_ = sequence_ = pcmBytes_ = 0;
    phase_ = "starting"; lastPhase_.clear(); endpoint_ = gptLive ? "gpt-live" : "realtime-live";
    photoRequested_ = photoReady_ = photoDeclined_ = listening_ = false; drainPending_ = drainAcknowledged_ = false;
    cancelled_.store(false); failed_.store(false); busy_.store(true); done_ = false;
  }
  worker_ = std::thread([this, request]() { Worker(request); });
  return true;
}
bool BrowserMedia::OwnsSession(const std::string& session) {
  std::lock_guard<std::mutex> lock(mutex_); return session == session_;
}
void BrowserMedia::Cancel() {
  std::lock_guard<std::mutex> lock(mutex_);
  cancelled_.store(true); ++epoch_; ++inputEpoch_; ++outputEpoch_;
  if (!audioObservation_.empty()) audioObservation_ = "browser-playback-cancelled";
  playbackPending_ = false;
  ClearLocked(); listening_ = false; photoRequested_ = false; drainPending_ = false; phase_ = "idle";
  output_.push_back({HeaderLocked("audio_cancel", 0, false, ""), {}, false});
  // Cancellation is nonblocking. Queue a single best-effort deletion for the
  // platform adapter, without making old results/current callbacks admissible.
  QueueDeleteLocked();
  changed_.notify_all();
}
void BrowserMedia::QueueDeleteLocked() {
  if (!conversation_.empty()) {
    const auto id = MediaUuid();
    const std::string header = "\"mediaServiceRequest\":{\"requestId\":" + Json(id) +
        ",\"kind\":\"conversation-delete\",\"method\":\"DELETE\",\"path\":" +
        Json("/v1/conversations/" + conversation_) + ",\"idempotencyKey\":" + Json(id) +
        ",\"headers\":{},\"contentType\":\"application/json\",\"byteLength\":0}";
    output_.push_back({header, {}, true}); conversation_.clear();
  }
}
std::string BrowserMedia::HeaderLocked(const std::string& kind, size_t bytes, bool drain, const std::string& text) {
  std::string header = "\"media\":{\"sessionId\":" + Json(session_) + ",\"bootId\":" + Json(boot_) +
      ",\"epoch\":" + Json(std::to_string(epoch_)) + ",\"inputEpoch\":" + Json(std::to_string(inputEpoch_)) +
      ",\"outputEpoch\":" + Json(std::to_string(outputEpoch_)) + ",\"sequence\":" + Json(std::to_string(sequence_)) +
      ",\"kind\":" + Json(kind) + ",\"byteLength\":" + std::to_string(bytes) + ",\"phase\":" + Json(phase_) +
      ",\"inputEnabled\":" + (listening_ ? "true" : "false") + ",\"drainedRequested\":" + (drain ? "true" : "false") + ",\"pcmBytes\":" + Json(std::to_string(pcmBytes_));
  if (!text.empty()) header += ",\"text\":" + Json(text);
  return header + "}";
}
std::string BrowserMedia::AudioObservation() {
  std::lock_guard<std::mutex> lock(mutex_); return audioObservation_;
}
std::string BrowserMedia::StateHeader() {
  std::lock_guard<std::mutex> lock(mutex_);
  return "\"mediaState\":{\"sessionId\":" + (session_.empty() ? "null" : Json(session_)) +
      ",\"bootId\":" + Json(boot_) + ",\"epoch\":" + Json(std::to_string(epoch_)) +
      ",\"inputEpoch\":" + Json(std::to_string(inputEpoch_)) + ",\"outputEpoch\":" + Json(std::to_string(outputEpoch_)) +
      ",\"phase\":" + Json(phase_) + ",\"inputEnabled\":" + (listening_ ? "true" : "false") + ",\"active\":" + (busy_.load() && !cancelled_.load() ? "true" : "false") +
      (photoWarning_.empty() ? "" : ",\"photoWarning\":" + Json(photoWarning_)) +
      (failed_.load() ? ",\"error\":\"media_failed\"" : "") + "}";
}
bool BrowserMedia::Emit(const std::string& kind, const std::vector<uint8_t>& bytes, bool drain, const std::string& text) {
  std::unique_lock<std::mutex> lock(mutex_);
  if (!Current()) return false;
  if (bytes.size() > 32768) { failed_.store(true); changed_.notify_all(); return false; }
  const auto canMerge = [&]() {
    return kind == "pcm" && !output_.empty() && output_.back().kind == "pcm"
        && output_.back().inputEpoch == inputEpoch_ && output_.back().outputEpoch == outputEpoch_
        && output_.back().bytes.size() + bytes.size() <= 32768;
  };
  // Only media workers call Emit. The owner/control path keeps consuming and
  // cancelling while the worker waits for a bounded browser playback queue.
  // Private services have their own two-request bound, so microphone upload
  // never waits for browser playback to make room in this HAL queue.
  if (!changed_.wait_for(lock, std::chrono::seconds(5), [&]() {
      return !Current() || std::count_if(output_.begin(), output_.end(), [](const Output& output) {
        return !output.service;
      }) < 32 || canMerge();
  })) { failed_.store(true); changed_.notify_all(); return false; }
  if (!Current()) return false;
  ++sequence_;
  if (kind == "pcm") {
    pcmBytes_ += bytes.size(); playbackPending_ = true; audioObservation_ = "browser-pcm-enqueued";
  }
  if (canMerge()) {
    auto& tail = output_.back();
    tail.bytes.insert(tail.bytes.end(), bytes.begin(), bytes.end());
    tail.header = HeaderLocked(kind, tail.bytes.size(), false, text);
  } else {
    output_.push_back({HeaderLocked(kind, bytes.size(), drain, text), bytes, false, kind, inputEpoch_, outputEpoch_});
  }
  if (drain) { drainSequence_ = sequence_; drainAcknowledged_ = false; drainPending_ = true; }
  changed_.notify_all(); return true;
}
void BrowserMedia::Phase(const char* phase) {
  {
    std::lock_guard<std::mutex> lock(mutex_);
    if (cancelled_.load()) return;
    phase_ = phase;
    if (lastPhase_ == phase && lastPhaseInputEpoch_ == inputEpoch_
        && lastPhaseOutputEpoch_ == outputEpoch_ && lastPhaseListening_ == listening_) return;
    lastPhase_ = phase; lastPhaseInputEpoch_ = inputEpoch_; lastPhaseOutputEpoch_ = outputEpoch_;
    lastPhaseListening_ = listening_;
  }
  // Photo's private phase uses the photo_request HAL event instead.
  const std::string value = phase;
  if (value == "listening" || value == "thinking" || value == "speaking") (void)Emit(value);
}
BrowserMedia::Response BrowserMedia::Request(const std::string& kind, const std::string& method,
    const std::string& path, const std::string& key, const std::string& headers, const char* contentType,
    const void* bytes, size_t size, unsigned timeoutMs) {
  Response result;
  std::unique_lock<std::mutex> lock(mutex_);
  if (!Current()) return result;
  if (requests_.size() >= 2 || size > (kind == "image-upload" ? 262144u : 32768u)) {
    failed_.store(true); return result;
  }
  const auto id = MediaUuid();
  auto pending = std::make_shared<Response>(); requests_[id] = pending;
  const std::string header = "\"mediaServiceRequest\":{\"requestId\":" + Json(id) + ",\"kind\":" + Json(kind) +
      ",\"method\":" + Json(method) + ",\"path\":" + Json(path) + ",\"idempotencyKey\":" + Json(key) +
      ",\"headers\":" + headers + ",\"contentType\":" + Json(contentType) + ",\"byteLength\":" + std::to_string(size) + "}";
  const auto* data = static_cast<const uint8_t*>(bytes);
  std::vector<uint8_t> attachment;
  if (size) attachment.assign(data, data + size);
  output_.push_back({header, std::move(attachment), true}); changed_.notify_all();
  changed_.wait_for(lock, std::chrono::milliseconds(timeoutMs), [&]() { return pending->ready || !Current(); });
  if (Current() && pending->ready) result = std::move(*pending);
  requests_.erase(id);
  // A service action not taken before timeout cannot be dispatched later.
  const auto stale = std::find_if(output_.begin(), output_.end(), [&id](const Output& output) {
    return output.service && output.header.find(id) != std::string::npos;
  });
  if (stale != output_.end()) { Wipe(stale->bytes); output_.erase(stale); changed_.notify_all(); }
  return result;
}
bool BrowserMedia::CloseConversation() {
  std::string conversation;
  { std::lock_guard<std::mutex> lock(mutex_); conversation = conversation_; }
  if (conversation.empty()) return true;
  const auto response = Request("conversation-delete", "DELETE", "/v1/conversations/" + conversation,
      MediaUuid(), "{}", "application/json", nullptr, 0);
  if (response.status != 204 && response.status != 404) return false;
  std::lock_guard<std::mutex> lock(mutex_); conversation_.clear(); return true;
}
void BrowserMedia::Begin() {
  std::lock_guard<std::mutex> lock(mutex_);
  if (!Current()) return;
  ++outputEpoch_; inputEpoch_ = 0; pcmBytes_ = 0; listening_ = false;
  playbackPending_ = false; audioObservation_ = "browser-playback-idle";
}
void BrowserMedia::Listen(bool enabled) {
  std::lock_guard<std::mutex> lock(mutex_);
  ++inputEpoch_; inputSequence_ = 0; listening_ = enabled && Current();
  for (auto& input : input_) Wipe(input);
  input_.clear(); inputBytes_ = 0;
  changed_.notify_all();
}
bool BrowserMedia::TakeInput(std::vector<uint8_t>& pcm) {
  std::unique_lock<std::mutex> lock(mutex_);
  changed_.wait_for(lock, std::chrono::milliseconds(10), [&]() { return !input_.empty() || !Current(); });
  if (!Current()) return false;
  pcm.clear();
  if (!input_.empty()) { pcm = std::move(input_.front()); input_.pop_front(); inputBytes_ -= pcm.size(); }
  return true;
}
bool BrowserMedia::TakeInputForEpoch(std::vector<uint8_t>& pcm,
    const RealtimeSessionEngine& engine, uint32_t engineEpoch) {
  std::unique_lock<std::mutex> lock(mutex_);
  pcm.clear();
  if (!Current()) return false;
  if (engine.InputEpoch() != engineEpoch) return true;
  const uint64_t halEpoch = inputEpoch_;
  changed_.wait_for(lock, std::chrono::milliseconds(10), [&]() {
    return !input_.empty() || !Current() || inputEpoch_ != halEpoch || engine.InputEpoch() != engineEpoch;
  });
  if (!Current()) return false;
  // Engine and HAL epochs are distinct: GPT pauses may refresh
  // Listen without changing the production engine's input turn. A stale wait
  // must leave the next HAL gate's first frame queued for its own iteration.
  if (inputEpoch_ != halEpoch || engine.InputEpoch() != engineEpoch) return true;
  if (!input_.empty()) { pcm = std::move(input_.front()); input_.pop_front(); inputBytes_ -= pcm.size(); }
  return true;
}
bool BrowserMedia::Play(const std::vector<uint8_t>& pcm) {
  if (pcm.empty() || pcm.size() % 2 || pcm.size() > 32768) return false;
  return Emit("pcm", pcm);
}
bool BrowserMedia::Drain() {
  if (!Emit("drain", {}, true)) return false;
  std::unique_lock<std::mutex> lock(mutex_);
  const uint64_t outputEpoch = outputEpoch_;
  changed_.wait_for(lock, std::chrono::seconds(30), [&]() {
    return drainAcknowledged_ || !Current() || outputEpoch != outputEpoch_;
  });
  const bool drained = Current() && outputEpoch == outputEpoch_ && drainAcknowledged_;
  if (drained) { playbackPending_ = false; audioObservation_ = "browser-playback-drained"; }
  else if (outputEpoch == outputEpoch_ && playbackPending_) audioObservation_ = "browser-playback-unknown";
  return drained;
}
void BrowserMedia::Stop() {
  std::lock_guard<std::mutex> lock(mutex_);
  listening_ = false; ++inputEpoch_;
  if (playbackPending_) audioObservation_ = "browser-playback-unknown";
  for (auto& input : input_) Wipe(input);
  input_.clear(); inputBytes_ = 0;
}
bool BrowserMedia::CancelPlayback() {
  { std::lock_guard<std::mutex> lock(mutex_);
    ++outputEpoch_; pcmBytes_ = 0; drainPending_ = false; drainAcknowledged_ = false;
    playbackPending_ = false; audioObservation_ = "browser-playback-cancelled";
    for (auto item = output_.begin(); item != output_.end();) {
      if (!item->service) { Wipe(item->bytes); item = output_.erase(item); } else ++item;
    }
  }
  changed_.notify_all(); return Emit("audio_cancel");
}
bool BrowserMedia::Capture(std::vector<uint8_t>& jpeg) {
  { std::lock_guard<std::mutex> lock(mutex_); photoRequested_ = true; photoReady_ = photoDeclined_ = false; jpegSequence_ = 0; Wipe(jpeg_); }
  if (!Emit("photo_request")) return false;
  std::unique_lock<std::mutex> lock(mutex_);
  changed_.wait_for(lock, std::chrono::seconds(120), [&]() { return photoReady_ || photoDeclined_ || !Current(); });
  photoRequested_ = false;
  if (!Current() || !photoReady_ || photoDeclined_) { Wipe(jpeg_); return false; }
  jpeg = std::move(jpeg_); return true;
}
bool BrowserMedia::Upload(const std::vector<uint8_t>& jpeg, std::string& operationId) {
  const auto& correlation = photoCorrelation_;
  const std::string headers = "{\"Client-Turn-Id\":" + Json(correlation.clientTurnId) +
      ",\"Boot-Id\":" + Json(boot_) + ",\"Generation\":" + Json(std::to_string(correlation.generation)) +
      ",\"Device-Revision\":" + Json(std::to_string(correlation.deviceRevision)) +
      ",\"Catalog-Version\":" + Json(snapshot_.catalogVersion) + "}";
  auto result = Request("image-upload", "POST", "/v1/conversations/" + correlation.conversationId + "/image",
      photoKey_, headers, "image/jpeg", jpeg.data(), jpeg.size());
  std::string json(result.bytes.begin(), result.bytes.end()); Wipe(result.bytes);
  const bool accepted = result.status == 202 && ParseImageAccepted(json, operationId);
  std::fill(json.begin(), json.end(), '\0'); return accepted;
}
PhotoPoll BrowserMedia::Poll(const std::string& operationId) {
  auto result = Request("image-poll", "GET", "/v1/operations/" + operationId, "", "{}", "application/json", nullptr, 0);
  std::string json(result.bytes.begin(), result.bytes.end()); Wipe(result.bytes);
  const auto state = result.status == 200 ? ParseImageOperation(json, operationId, photoCorrelation_) : PhotoPoll::Failed;
  std::fill(json.begin(), json.end(), '\0'); return state;
}
bool BrowserMedia::TakeService(std::string& extension, std::vector<uint8_t>& bytes) {
  std::lock_guard<std::mutex> lock(mutex_);
  auto item = std::find_if(output_.begin(), output_.end(), [](const Output& output) { return output.service; });
  if (item == output_.end()) return false;
  extension = std::move(item->header); bytes = std::move(item->bytes); output_.erase(item);
  changed_.notify_all(); return true;
}
bool BrowserMedia::Handle(const std::vector<std::string>& words, const std::vector<uint8_t>& body,
    std::string& extension, std::vector<uint8_t>& attachment, std::string* rejectionCode) {
  if (rejectionCode) rejectionCode->clear();
  std::unique_lock<std::mutex> lock(mutex_);
  const auto& command = words[0];
  if (command == "MEDIA_RESULT" && words.size() == 5) {
    uint64_t status = 0;
    auto request = requests_.find(words[1]);
    if (request == requests_.end() || request->second->ready || !Decimal(words[2], status) || status > 599 ||
        body.size() > 65536 || !Current()) return false;
    request->second->status = static_cast<int>(status); request->second->type = words[3];
    request->second->bytes = body; request->second->ready = true; changed_.notify_all(); return true;
  }
  uint64_t epoch = 0;
  if (words.size() < 3 || words[1] != session_ || !Decimal(words[2], epoch) || epoch != epoch_) return false;
  if (command == "MEDIA_TAKE" && words.size() == 3) {
    auto item = std::find_if(output_.begin(), output_.end(), [](const Output& output) { return !output.service; });
    if (item != output_.end()) {
      extension = std::move(item->header); attachment = std::move(item->bytes); output_.erase(item); changed_.notify_all();
    }
    return true;
  }
  if (command == "MEDIA_FRAME" && words.size() == 7 && words[5] == "pcm") {
    uint64_t inputEpoch = 0, sequence = 0;
    if (!Decimal(words[3], inputEpoch) || !Decimal(words[4], sequence)
        || body.empty() || body.size() > 9600 || body.size() % 2
        || cancelled_.load() || failed_.load()) return false;
    // A microphone request can already be in flight when VAD closes Listen.
    // Retired input is discarded without admitting bytes or advancing sequence;
    // a current listening gate still requires an exact, uninterrupted sequence.
    if ((!busy_.load() && done_ && phase_ == "completed" && inputEpoch <= inputEpoch_)
        || (Current() && busy_.load() && (inputEpoch < inputEpoch_ || (inputEpoch == inputEpoch_ && !listening_)))) {
      if (rejectionCode) *rejectionCode = "media-input-retired";
      return false;
    }
    if (!Current() || !busy_.load() || inputEpoch != inputEpoch_) return false;
    if (sequence != inputSequence_) {
      if (rejectionCode) *rejectionCode = "media-input-sequence-mismatch";
      return false;
    }
    if (inputBytes_ + body.size() > 19200) {
      if (rejectionCode) *rejectionCode = "media-input-overflow";
      failed_.store(true); changed_.notify_all(); return false;
    }
    ++inputSequence_; inputBytes_ += body.size(); input_.push_back(body); changed_.notify_all(); return true;
  }
  if (!Current() || !busy_.load()) return false;
  if (command == "MEDIA_EVENT" && words.size() == 4) {
    if (body.empty() || body.size() > 65536) return false;
    if (events_.size() >= 8 || eventBytes_ + body.size() > 524288) {
      if (rejectionCode) *rejectionCode = "media-event-backpressure";
      return false;
    }
    events_.push_back(body); eventBytes_ += body.size(); changed_.notify_all(); return true;
  }
  if (command == "MEDIA_FRAME" && words.size() == 7) {
    uint64_t inputEpoch = 0, sequence = 0;
    if (!Decimal(words[3], inputEpoch) || !Decimal(words[4], sequence)) return false;
    if (words[5] == "jpeg") {
      if (!photoRequested_ || photoReady_ || inputEpoch != inputEpoch_ || sequence != jpegSequence_ ||
          body.size() > 32768 || jpeg_.size() + body.size() > 262144) return false;
      if (body.empty()) { photoDeclined_ = true; Wipe(jpeg_); changed_.notify_all(); return true; }
      ++jpegSequence_; jpeg_.insert(jpeg_.end(), body.begin(), body.end());
      if (jpeg_.size() >= 2 && jpeg_[jpeg_.size() - 2] == 255 && jpeg_.back() == 217) photoReady_ = true;
      changed_.notify_all(); return true;
    }
    return false;
  }
  if (command == "MEDIA_PLAYED" && words.size() == 7) {
    uint64_t outputEpoch = 0, sequence = 0, bytes = 0;
    if (!Decimal(words[3], outputEpoch) || !Decimal(words[4], sequence) || !Decimal(words[5], bytes) ||
        outputEpoch != outputEpoch_ || sequence > sequence_ || bytes > pcmBytes_ || (words[6] != "true" && words[6] != "false")) return false;
    if (words[6] == "true") {
      if (!drainPending_ || sequence != drainSequence_ || bytes != pcmBytes_ || drainAcknowledged_) return false;
      drainAcknowledged_ = true; drainPending_ = false; changed_.notify_all();
    }
    return true;
  }
  return false;
}
bool BrowserMedia::TakeCompletion(MediaCompletion& completion) {
  std::lock_guard<std::mutex> lock(mutex_);
  if (!completion_ || cancelled_.load()) return false;
  completion = std::move(*completion_); completion_.reset(); return true;
}
void BrowserMedia::Worker(VoiceWorkflowRequest request) {
  VoiceWorkflowResult result;
  try {
    VoiceWorkflowPorts ports;
    ports.current = [this]() { return Current(); }; ports.uuid = MediaUuid;
    ports.close = [this]() { return CloseConversation(); };
    ports.create = [this](bool photo, const std::string& key, std::string& conversation) {
      ConversationStartRequest start;
      start.deviceId = device_; start.bootId = boot_; start.idempotencyKey = key;
      start.generation = snapshot_.generation; start.catalogVersion = snapshot_.catalogVersion;
      start.audioConsent = true; start.imageConsent = photo;
      std::string body;
      if (!BuildConversationStartRequest(start, body)) return false;
      auto response = Request("conversation-create", "POST", "/v1/conversations", key, "{}", "application/json", body.data(), body.size());
      std::fill(body.begin(), body.end(), '\0');
      ConversationStartResponse created;
      const bool valid = response.status == 201 && response.type.find("application/json") == 0 &&
          ParseConversationStartResponse(reinterpret_cast<const char*>(response.bytes.data()), response.bytes.size(), created);
      Wipe(response.bytes);
      if (!valid) return false;
      { std::lock_guard<std::mutex> lock(mutex_); conversation_ = created.conversationId; }
      conversation = created.conversationId; return true;
    };
    ports.photo = [this](const TurnCorrelation& correlation) {
      photoCorrelation_ = correlation; photoKey_ = MediaUuid();
      BrowserCamera camera(*this, cameraAvailable_);
      const auto photo = PrepareCostumePhoto(true, camera, *this, [this]() { return Current(); }, MediaWallMs,
          [this]() { std::unique_lock<std::mutex> lock(mutex_); changed_.wait_for(lock, std::chrono::milliseconds(250), [this]() { return !Current(); }); },
          [this](const char* phase) { Phase(phase); });
      { std::lock_guard<std::mutex> lock(mutex_);
        photoWarning_ = photo == PhotoOutcome::Unavailable ? "photo_unavailable" : photo == PhotoOutcome::Failed ? "photo_failed" : "";
      }
      return photo;
    };
    ports.beginAudio = [this]() { Begin(); };
    ports.realtime = [this](const std::string& body, const std::string& key, const TurnCorrelation& correlation,
        const std::function<bool(const TurnOperationObservation&)>& completed) {
      RealtimeSessionEngine engine(*this, endpoint_ == "gpt-live", [this]() { return Current(); }, MediaWallMs, Utc,
          [this](const char* phase) { Phase(phase); },
          [this](const std::string& path, const void* data, size_t size, const char* type, const std::string& inputId, unsigned sequence) {
            std::string headers = "{}", kind = "realtime-played";
            if (!inputId.empty()) { kind = "realtime-input"; headers = "{\"Input-Id\":" + Json(inputId) + ",\"Input-Sequence\":" + Json(std::to_string(sequence)) + "}"; }
            else if (path.size() >= 9 && path.substr(path.size() - 9) == "/playback") kind = "realtime-playback";
            return Request(kind, "POST", path, "", headers, type, data, size, 3000).status;
          }, correlation, snapshot_, completed);
      auto response = Request("realtime-start", "POST", "/v1/conversations/" + correlation.conversationId + "/" + endpoint_,
          key, "{}", "application/json", body.data(), body.size());
      Wipe(response.bytes);
      if (response.status != 200 || response.type.find("application/x-ndjson") != 0) return false;
      std::thread upload([&engine, this]() {
        std::vector<uint8_t> pcm;
        try {
          while (engine.Current()) {
            if (!engine.Uploading()) { std::unique_lock<std::mutex> lock(mutex_); changed_.wait_for(lock, std::chrono::milliseconds(10)); continue; }
            const uint32_t inputEpoch = engine.InputEpoch();
            if (!TakeInputForEpoch(pcm, engine, inputEpoch)) { engine.Fail(); break; }
            if (!pcm.empty()) (void)engine.SendInput(pcm, inputEpoch);
            Wipe(pcm);
          }
        } catch (...) { engine.Fail(); }
        Wipe(pcm);
      });
      bool valid = true; size_t total = 0;
      try {
        while (engine.Current() && !engine.Closed() && valid) {
          std::vector<uint8_t> payload;
          { std::unique_lock<std::mutex> lock(mutex_);
            changed_.wait_for(lock, std::chrono::milliseconds(100), [&]() { return !events_.empty() || !engine.Current(); });
            if (events_.empty()) continue;
            payload = std::move(events_.front()); events_.pop_front(); eventBytes_ -= payload.size(); changed_.notify_all();
          }
          total += payload.size();
          std::string line(payload.begin(), payload.end()); Wipe(payload);
          if (!line.empty() && line.back() == '\n') line.pop_back();
          if (!line.empty() && line.back() == '\r') line.pop_back();
          RealtimeEvent event;
          valid = total <= 20000000 && ParseRealtimeEvent(line, event) && engine.Event(event);
          const bool currentResponse = event.hasResponseGeneration
              ? event.responseGeneration == engine.ResponseGeneration() : engine.ResponseGeneration() == 0;
          // Ignored old provider events still return true. Fence every outward
          // projection here; visitor text is never projected by this adapter.
          if (valid && currentResponse) {
            if (event.type == "completed" && !event.operation.reply.empty())
              (void)Emit("dialogue", {}, false, event.operation.reply);
          }
          std::fill(line.begin(), line.end(), '\0'); std::fill(event.delta.begin(), event.delta.end(), '\0');
          std::fill(event.operation.reply.begin(), event.operation.reply.end(), '\0');
        }
      } catch (...) { valid = false; engine.Fail(); }
      const bool succeeded = valid && engine.Closed() && engine.Current();
      engine.Stop(); changed_.notify_all(); upload.join(); return succeeded;
    };
    result = RunVoiceWorkflow(request, ports);
    Stop();
    if (!result.succeeded || !result.final.hasProposal) (void)CloseConversation();
  } catch (...) { failed_.store(true); }
  {
    std::lock_guard<std::mutex> lock(mutex_);
    if (result.succeeded && Current() && std::count_if(output_.begin(), output_.end(), [](const Output& output) {
        return !output.service;
    }) < 32) {
      phase_ = "completed"; ++sequence_;
      output_.push_back({HeaderLocked("completed", 0, false, ""), {}, false});
      if (result.final.hasProposal) completion_.reset(new MediaCompletion{snapshot_, std::move(result.final), epoch_});
    } else if (!cancelled_.load()) {
      failed_.store(true); phase_ = "failed"; listening_ = false; ++outputEpoch_;
      if (!audioObservation_.empty()) audioObservation_ = "browser-playback-unknown";
      playbackPending_ = false;
      for (auto item = output_.begin(); item != output_.end();) {
        if (!item->service) { Wipe(item->bytes); item = output_.erase(item); } else ++item;
      }
      output_.push_back({HeaderLocked("failed", 0, false, ""), {}, false});
      QueueDeleteLocked();
    }
    busy_.store(false); done_ = true; changed_.notify_all();
  }
  std::fill(result.final.reply.begin(), result.final.reply.end(), '\0');
}

#include "RealtimeSessionEngine.h"
#include <algorithm>
#include <utility>

bool DecodeRealtimePcm(const std::string& encoded, uint8_t* output, size_t capacity, size_t& length) {
  length = 0;
  if (encoded.empty() || encoded.size() % 4) return false;
  const std::string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
  for (size_t offset = 0; offset < encoded.size(); offset += 4) {
    unsigned value = 0, padding = 0;
    for (size_t index = 0; index < 4; ++index) {
      const char byte = encoded[offset + index];
      if (byte == '=') {
        if (index < 2 || offset + 4 != encoded.size()) return false;
        ++padding; value <<= 6;
      } else {
        const size_t digit = alphabet.find(byte);
        if (padding || digit == std::string::npos) return false;
        value = (value << 6) | static_cast<unsigned>(digit);
      }
    }
    if (padding > 2 || length + 3 - padding > capacity) return false;
    output[length++] = static_cast<uint8_t>(value >> 16);
    if (padding < 2) output[length++] = static_cast<uint8_t>(value >> 8);
    if (!padding) output[length++] = static_cast<uint8_t>(value);
  }
  return true;
}
RealtimeSessionEngine::RealtimeSessionEngine(VoiceAudioPort& audio, bool gptLive,
    std::function<bool()> current, std::function<uint64_t()> now,
    std::function<uint64_t()> utc, std::function<void(const char*)> phase,
    PostPort post, const TurnCorrelation& opening,
    const ConversationRuntimeSnapshot& snapshot,
    std::function<bool(const TurnOperationObservation&)> completed)
    : audio_(audio), gptLive_(gptLive), endpoint_(gptLive ? "gpt-live" : "realtime-live"),
      conversationId_(opening.conversationId), current_(std::move(current)),
      now_(std::move(now)), utc_(std::move(utc)), phase_(std::move(phase)),
      post_(std::move(post)), expected_(opening), snapshot_(snapshot), completed_(std::move(completed)) {
  startedAtMs_.store(now_()); lastTurnAtMs_.store(now_());
}
bool RealtimeSessionEngine::Current() const {
  return !stop_.load() && !failed_.load() && !audio_.Failed() && current_() &&
      expected_.operationEpoch == snapshot_.operationEpoch &&
      now_() - startedAtMs_ < 480000 && now_() - lastTurnAtMs_ < (gptLive_ ? 190000 : 80000);
}
int RealtimeSessionEngine::Post(const std::string& path, const void* data, size_t size,
    const char* type, const std::string& inputId, unsigned sequence) {
  return Current() ? post_(path, data, size, type, inputId, sequence) : 0;
}
bool RealtimeSessionEngine::SendInput(const std::vector<uint8_t>& pcm, uint32_t capturedEpoch) {
  std::lock_guard<std::mutex> lock(inputMutex_);
  if (!uploading_.load() || capturedEpoch != inputEpoch_.load() || !Current()) return true;
  const std::string path = "/v1/conversations/" + conversationId_ + "/" + endpoint_ + "/" + streamId_ + "/audio";
  inputBytes_ += pcm.size();
  if (inputBytes_ > (gptLive_ ? 8640000u : 1440000u) ||
      Post(path, pcm.data(), pcm.size(), "application/octet-stream", inputId_, sequence_++) != 204) {
    failed_.store(true); return false;
  }
  return true;
}
bool RealtimeSessionEngine::Event(const RealtimeEvent& event) {
  if (!Current()) return false;
  if (event.type == "failed") {
    error_ = event.code == "cost_limit" ? "cost_limit" : "ai_unavailable";
    return false;
  }
  if (event.type == "heartbeat") return sessionSeen_;
  if (event.type == "session") {
    if (sessionSeen_) return false;
    sessionSeen_ = true; streamId_ = event.streamId; return true;
  }
  if (!sessionSeen_) return false;
  const bool generationBound = event.type == "audio" || event.type == "started" ||
      event.type == "completed" || event.type == "listening" || event.type == "visitor" ||
      event.type == "pause" || event.type == "playback_checkpoint" ||
      event.type == "speech_started" || event.type == "speech_stopped" || event.type == "closed";
  if (generationBound) {
    if ((!event.hasResponseGeneration && responseGeneration_ > 0) ||
        (event.hasResponseGeneration && event.responseGeneration < responseGeneration_)) return true;
    if (event.responseGeneration > responseGeneration_) return false;
  }
  if (event.type == "listening") {
    // Context refresh can repeat the same listen event. Captured PCM keeps its
    // input epoch and sequence; it must never be relabelled as fresh input.
    if (uploading_.load() && event.inputId == inputId_) return true;
    if (terminal_ || (!gptLive_ && !operationId_.empty()) || uploading_.load()) return false;
    std::lock_guard<std::mutex> lock(inputMutex_);
    expected_.clientTurnId = event.inputId;
    inputId_ = event.inputId; sequence_ = 0; inputBytes_ = 0;
    inputEpoch_.fetch_add(1);
    lastTurnAtMs_ = now_();
    audio_.Listen(true); uploading_.store(true); phase_("listening"); return true;
  }
  if (event.type == "speech_started") return uploading_.load() && event.inputId == inputId_;
  if (event.type == "speech_stopped") {
    if (!uploading_.load() || event.inputId != inputId_) return false;
    uploading_.store(false); inputEpoch_.fetch_add(1);
    audio_.Listen(false); phase_("thinking"); return true;
  }
  if (event.type == "started") {
    const bool restarting = !gptLive_ && responseRestartPending_ && event.operationId == operationId_;
    if (terminal_ || (!operationId_.empty() && !restarting) || uploading_.load()) return false;
    const auto decision = EvaluateTurnOperation(event.operationId, expected_, snapshot_.generation,
                                               snapshot_.deviceRevision, event.operation, snapshot_.operationEpoch);
    if (decision != TurnPollDecision::ReplyReady) return false;
    if (!restarting && ++turns_ > 10) return false;
    responseRestartPending_ = false;
    operationId_ = event.operationId; audioBytes_ = 0; phase_("thinking"); return true;
  }
  if (event.type == "visitor") return !operationId_.empty();
  if (event.type == "audio_cancel") {
    if (!event.responseGeneration || !Current()) return false;
    if (event.responseGeneration <= responseGeneration_) return true;
    if (!audio_.CancelPlayback()) return false;
    responseGeneration_ = event.responseGeneration;
    responseRestartPending_ = !gptLive_ && !operationId_.empty();
    segmentPlaying_ = false;
    audio_.Listen(uploading_.load());
    phase_(uploading_.load() ? "listening" : "thinking");
    return true;
  }
  if (event.type == "audio") {
    if (operationId_.empty() || responseRestartPending_) return false;
    std::vector<uint8_t> pcm(32768);
    size_t size = 0;
    const bool decoded = DecodeRealtimePcm(event.delta, pcm.data(), pcm.size(), size);
    audioBytes_ += size;
    if (!decoded || !size || size % 2 || audioBytes_ > (gptLive_ ? 9120000u : 1440000u)) {
      std::fill(pcm.begin(), pcm.end(), 0); return false;
    }
    pcm.resize(size); segmentPlaying_ = true; phase_("speaking");
    const bool played = Current() && audio_.Play(pcm);
    std::fill(pcm.begin(), pcm.end(), 0);
    return played;
  }
  if (event.type == "pause") {
    if (!gptLive_ || operationId_.empty()) return false;
    if (segmentPlaying_) {
      if (!audio_.Drain() || !Current()) return false;
      segmentPlaying_ = false;
    }
    audio_.Listen(uploading_.load());
    phase_("listening"); return true;
  }
  if (event.type == "completed") {
    if (operationId_.empty() || responseRestartPending_ || event.operationId != operationId_ || audioBytes_ == 0 || !Current()) return false;
    const uint64_t utc = utc_();
    if (utc <= 0) return false;
    const auto decision = EvaluateTurnOperationWithProposal(operationId_, expected_, snapshot_.generation,
        snapshot_.deviceRevision, snapshot_.catalogVersion, snapshot_.readyMask,
        static_cast<uint64_t>(utc), event.operation, snapshot_.operationEpoch);
    if (decision != TurnPollDecision::ReplyReady && decision != TurnPollDecision::ProposalReady) return false;
    if ((!gptLive_ || segmentPlaying_) && !audio_.Drain()) return false;
    if (!Current() || !completed_(event.operation)) return false;
    const std::string body = "{\"operationId\":\"" + operationId_ + "\",\"responseGeneration\":" +
        std::to_string(responseGeneration_) + "}";
    const std::string path = "/v1/conversations/" + conversationId_ + "/" + endpoint_ + "/" + streamId_ + "/played";
    if (Post(path, body.data(), body.size(), "application/json") != 204) return false;
    terminal_ = event.operation.decision != "ask" || turns_ == 10;
    operationId_.clear(); phase_(terminal_ ? "completed" : "thinking"); return true;
  }
  if (event.type == "playback_checkpoint") {
    if (!gptLive_ || operationId_.empty() || event.pcmBytes != audioBytes_ || !Current()) return false;
    if (segmentPlaying_) {
      if (!audio_.Drain() || !Current()) return false;
      segmentPlaying_ = false;
    }
    // Drain confirms the local DMA tail, not a physical presentation. Keep the
    // safe microphone gate until Drain; ordinary pause never authorizes a servo.
    const std::string body = "{\"playbackId\":\"" + event.playbackId + "\",\"pcmBytes\":" +
        std::to_string(event.pcmBytes) + ",\"drained\":true}";
    const std::string path = "/v1/conversations/" + conversationId_ + "/gpt-live/" + streamId_ + "/playback";
    if (Post(path, body.data(), body.size(), "application/json") != 204)
      (void)Post(path, body.data(), body.size(), "application/json"); // same idempotent ACK, no operation replay
    phase_("listening"); return Current();
  }
  if (event.type == "closed") {
    if (!terminal_ || !operationId_.empty()) return false;
    closed_ = true; return true;
  }
  return false;
}

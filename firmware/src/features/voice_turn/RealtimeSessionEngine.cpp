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
RealtimeSessionEngine::RealtimeSessionEngine(VoiceAudioPort& audio, bool gptLive,
    std::function<bool()> current, std::function<uint64_t()> now,
    std::function<uint64_t()> utc, std::function<void(const char*)> phase,
    PostPort post, const TurnCorrelation& opening,
    const ConversationRuntimeSnapshot& snapshot,
    std::function<bool(const TurnOperationObservation&)> completed, AsyncPorts ports)
    : RealtimeSessionEngine(audio, gptLive, std::move(current), std::move(now),
          std::move(utc), std::move(phase), std::move(post), opening, snapshot, std::move(completed)) {
  playback_ = &ports.playback;
  control_ = &ports.control;
  capture_ = std::move(ports.capture);
  if (!capture_) Fail();
}
void RealtimeSessionEngine::Capture(bool enabled) {
  if (playback_) capture_(enabled);
  else audio_.Listen(enabled);
}
void RealtimeSessionEngine::Stop() {
  stop_.store(true); uploading_.store(false); inputEpoch_.fetch_add(1);
  if (playback_) {
    if (capture_) capture_(false);
    playback_->Stop(); control_->Stop();
  }
}
void RealtimeSessionEngine::Fail() {
  failed_.store(true);
  if (playback_) Stop();
}
bool RealtimeSessionEngine::Current() const {
  return !stop_.load() && !failed_.load() && (playback_ ? !playback_->Failed() : !audio_.Failed()) && current_() &&
      expected_.operationEpoch == snapshot_.operationEpoch &&
      now_() - startedAtMs_ < 480000 && now_() - lastTurnAtMs_ < (gptLive_ ? 190000 : 80000);
}
int RealtimeSessionEngine::Post(const std::string& path, const void* data, size_t size,
    const char* type, const std::string& inputId, unsigned sequence) {
  return Current() ? post_(path, data, size, type, inputId, sequence) : 0;
}
bool RealtimeSessionEngine::SendInput(const std::vector<uint8_t>& pcm, uint32_t capturedEpoch) {
  std::string path, input;
  unsigned sequence;
  uint64_t claim;
  {
    std::lock_guard<std::mutex> lock(inputMutex_);
    if (!uploading_.load() || capturedEpoch != inputEpoch_.load() || !Current()) return true;
    if (inputInFlight_) return false;
    const size_t limit = gptLive_ ? 8640000u : 1440000u;
    if (pcm.empty() || pcm.size() % 2 || pcm.size() > 32768 || pcm.size() > limit - inputBytes_) {
      failed_.store(true); return false;
    }
    path = "/v1/conversations/" + conversationId_ + "/" + endpoint_ + "/" + streamId_ + "/audio";
    input = inputId_; sequence = sequence_++;
    inputBytes_ += pcm.size(); inputInFlight_ = true; claim = ++inputClaim_;
  }
  const int status = Post(path, pcm.data(), pcm.size(), "application/octet-stream", input, sequence);
  {
    std::lock_guard<std::mutex> lock(inputMutex_);
    if (inputClaim_ == claim) inputInFlight_ = false;
  }
  if (status != 204) { Fail(); return false; }
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
    {
      std::lock_guard<std::mutex> lock(inputMutex_);
      if (uploading_.load() && event.inputId == inputId_) return true;
      if (terminal_ || (!gptLive_ && !operationId_.empty()) || uploading_.load()) return false;
      expected_.clientTurnId = event.inputId;
      inputId_ = event.inputId; sequence_ = 0; inputBytes_ = 0;
      inputEpoch_.fetch_add(1);
      uploading_.store(true);
    }
    lastTurnAtMs_ = now_();
    Capture(true); phase_("listening"); return true;
  }
  if (event.type == "speech_started") return uploading_.load() && event.inputId == inputId_;
  if (event.type == "speech_stopped") {
    if (!uploading_.load() || event.inputId != inputId_) return false;
    uploading_.store(false); inputEpoch_.fetch_add(1);
    Capture(false); phase_("thinking"); return true;
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
    if (playback_) {
      AbandonActions();
      closedPending_ = false;
      inputEpoch_.fetch_add(1);
      const auto cancelled = playback_->Cancel(event.responseGeneration);
      if (cancelled == VoicePlaybackPort::Progress::Failed) { Fail(); return false; }
      cancelPending_ = cancelled == VoicePlaybackPort::Progress::Pending;
    } else if (!audio_.CancelPlayback()) return false;
    responseGeneration_ = event.responseGeneration;
    responseRestartPending_ = !gptLive_ && !operationId_.empty();
    segmentPlaying_ = false;
    Capture(uploading_.load());
    phase_(uploading_.load() ? "listening" : "thinking");
    return true;
  }
  if (event.type == "audio") {
    if (operationId_.empty() || responseRestartPending_) return false;
    std::vector<uint8_t> pcm(32768);
    size_t size = 0;
    const bool decoded = DecodeRealtimePcm(event.delta, pcm.data(), pcm.size(), size);
    if (!decoded || !size || size % 2) {
      std::fill(pcm.begin(), pcm.end(), 0); return false;
    }
    if (playback_) {
      const bool queued = Audio(responseGeneration_, pcm.data(), size);
      std::fill(pcm.begin(), pcm.end(), 0);
      return queued;
    }
    audioBytes_ += size;
    if (audioBytes_ > (gptLive_ ? 9120000u : 1440000u)) { std::fill(pcm.begin(), pcm.end(), 0); return false; }
    pcm.resize(size); segmentPlaying_ = true; phase_("speaking");
    const bool played = Current() && audio_.Play(pcm);
    std::fill(pcm.begin(), pcm.end(), 0);
    return played;
  }
  if (event.type == "pause") {
    if (!gptLive_ || operationId_.empty()) return false;
    if (playback_) return AdmitAction(ActionKind::Pause, event);
    if (segmentPlaying_) {
      if (!audio_.Drain() || !Current()) return false;
      segmentPlaying_ = false;
    }
    audio_.Listen(uploading_.load());
    phase_("listening"); return true;
  }
  if (event.type == "completed") {
    if (operationId_.empty() || responseRestartPending_ || event.operationId != operationId_ || audioBytes_ == 0 || !Current()) return false;
    if (playback_) return AdmitAction(ActionKind::Completed, event);
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
    if (playback_) return AdmitAction(ActionKind::Checkpoint, event);
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
    if (playback_ && actionCount_ != 0) { closedPending_ = true; return true; }
    if (!terminal_ || !operationId_.empty()) return false;
    closed_ = true; return true;
  }
  return false;
}

bool RealtimeSessionEngine::Audio(uint64_t generation, const uint8_t* pcm, size_t length) {
  if (!playback_ || !Current() || !sessionSeen_) return false;
  if (generation < responseGeneration_) return true;
  for (size_t index = 0; index != actionCount_; ++index) {
    if (actions_[index].kind == ActionKind::Completed) { Fail(); return false; }
  }
  if (generation != responseGeneration_ || operationId_.empty() || responseRestartPending_
      || !pcm || !length || length % 2 || length > 32768) return false;
  const size_t limit = gptLive_ ? 9120000u : 1440000u;
  if (length > limit - audioBytes_ || !playback_->Queue(generation, pcm, length)) {
    Fail(); return false;
  }
  audioBytes_ += length; ++audioRevision_; segmentPlaying_ = true;
  if (!gptLive_) Capture(false);
  phase_("speaking");
  return true;
}

bool RealtimeSessionEngine::ValidCompleted(const TurnOperationObservation& operation,
    const TurnCorrelation& expected) const {
  if (!Current() || !utc_()) return false;
  const auto result = EvaluateTurnOperationWithProposal(operationId_, expected,
      snapshot_.generation, snapshot_.deviceRevision, snapshot_.catalogVersion,
      snapshot_.readyMask, utc_(), operation, snapshot_.operationEpoch);
  return result == TurnPollDecision::ReplyReady || result == TurnPollDecision::ProposalReady;
}

bool RealtimeSessionEngine::AdmitAction(ActionKind kind, const RealtimeEvent& event) {
  for (size_t index = 0; index != actionCount_; ++index) {
    if (actions_[index].kind == ActionKind::Completed) { Fail(); return false; }
  }
  if (actionCount_ == actions_.size()) { Fail(); return false; }
  if (kind == ActionKind::Completed) {
    if (!ValidCompleted(event.operation, expected_)) return false;
    for (size_t index = 0; index < actionCount_; ++index) {
      if (actions_[index].kind == ActionKind::Completed) return false;
    }
  }
  if (kind == ActionKind::Checkpoint && (event.playbackId.empty() || event.playbackId.size() > 128)) return false;
  Action action;
  action.kind = kind; action.generation = responseGeneration_;
  action.admittedAt = now_(); action.turnStartedAt = lastTurnAtMs_.load();
  action.audioRevision = audioRevision_; action.expected = expected_;
  action.command.operationId = operationId_;
  action.command.responseGeneration = responseGeneration_;
  action.command.pcmBytes = audioBytes_;
  if (kind == ActionKind::Completed) {
    action.command.kind = VoiceControlPort::Kind::Played;
    action.operation = event.operation;
  } else if (kind == ActionKind::Checkpoint) {
    action.command.kind = VoiceControlPort::Kind::Checkpoint;
    action.command.playbackId = event.playbackId;
  }
  // Request at admission, before any later PCM may change the accepted frontier.
  if (!playback_->RequestDrain(action.generation, action.drain) || action.drain <= lastDrainHandle_) {
    Fail(); return false;
  }
  lastDrainHandle_ = action.drain;
  actions_[actionCount_++] = std::move(action);
  return true;
}

void RealtimeSessionEngine::PopAction() {
  if (actions_[0].drain) playback_->AbandonDrain(actions_[0].drain);
  if (actions_[0].control) control_->Abandon(actions_[0].control);
  for (size_t index = 1; index < actionCount_; ++index) actions_[index - 1] = std::move(actions_[index]);
  actions_[--actionCount_] = Action{};
}

void RealtimeSessionEngine::AbandonActions() {
  while (actionCount_) PopAction();
}

bool RealtimeSessionEngine::Poll() {
  if (!playback_) return Current();
  if (!Current()) { AbandonActions(); Fail(); return false; }
  if (cancelPending_) {
    const auto progress = playback_->Cancel(responseGeneration_);
    if (progress == VoicePlaybackPort::Progress::Failed) { AbandonActions(); Fail(); return false; }
    if (progress == VoicePlaybackPort::Progress::Pending) return true;
    cancelPending_ = false;
  }
  if (actionCount_) {
    auto& action = actions_[0];
    const auto now = now_();
    if (action.generation != responseGeneration_ || now - action.admittedAt >= 35000
        || now - action.turnStartedAt >= (gptLive_ ? 190000u : 80000u)) {
      AbandonActions(); Fail(); return false;
    }
    if (!action.drained) {
      const auto progress = playback_->PollDrain(action.drain);
      if (progress == VoicePlaybackPort::Progress::Pending) return true;
      if (progress == VoicePlaybackPort::Progress::Failed || !Current()) {
        AbandonActions(); Fail(); return false;
      }
      action.drained = true;
      if (action.kind == ActionKind::Completed && !ValidCompleted(action.operation, action.expected)) {
        AbandonActions(); Fail(); return false;
      }
      if (action.kind == ActionKind::Pause) {
        if (action.audioRevision == audioRevision_) {
          segmentPlaying_ = false; Capture(uploading_.load()); phase_("listening");
        }
        PopAction();
        return true;
      }
    }
    if (!action.control) {
      if (!control_->Submit(action.command, action.control) || action.control <= lastControlHandle_) {
        AbandonActions(); Fail(); return false;
      }
      lastControlHandle_ = action.control;
      return true;
    }
    const auto progress = control_->Poll(action.control);
    if (progress == VoiceControlPort::Progress::Pending) return true;
    if (progress == VoiceControlPort::Progress::Failed || !Current()
        || (action.kind == ActionKind::Completed && !ValidCompleted(action.operation, action.expected))) {
      AbandonActions(); Fail(); return false;
    }
    const Action settled = action;
    PopAction();
    if (settled.audioRevision == audioRevision_) segmentPlaying_ = false;
    if (settled.kind == ActionKind::Completed) {
      // Typed played success precedes the sole domain publication. The original
      // observation is checked with a fresh clock again after that exact ACK.
      if (!completed_(settled.operation)) { AbandonActions(); Fail(); return false; }
      terminal_ = settled.operation.decision != "ask" || turns_ == 10;
      operationId_.clear(); phase_(terminal_ ? "completed" : "thinking");
    } else {
      if (!segmentPlaying_) Capture(uploading_.load());
      phase_("listening");
    }
  }
  if (!actionCount_ && closedPending_) {
    if (!terminal_ || !operationId_.empty()) { Fail(); return false; }
    closedPending_ = false; closed_ = true;
  }
  return true;
}

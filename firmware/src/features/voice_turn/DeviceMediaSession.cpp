#include "DeviceMediaSession.h"
#include "../conversation/ConversationStartCodec.h"
#include <algorithm>
#include <cstring>

namespace {
void Clear(std::string& value) { std::fill(value.begin(), value.end(), '\0'); value.clear(); }
bool CanonicalUuid(const std::string& value) {
  std::string normalized;
  return NormalizeConversationUuid(value, normalized) && value == normalized;
}
}
DeviceMediaSession::DeviceMediaSession(DeviceMediaSocketPort& socket, Clock now,
    std::function<std::string()> requestId, Capture capture, uint8_t* workspace, size_t capacity)
    : socket_(socket), now_(std::move(now)), requestId_(std::move(requestId)),
      capture_(std::move(capture)), receive_(workspace), receiveCapacity_(capacity) {
  captured_.reserve(4800);
}
DeviceMediaSession::~DeviceMediaSession() {
  Stop(); ClearInput();
  for (auto& reply : replies_) { Clear(reply.wire); Clear(reply.id); }
  if (receive_) std::fill(receive_, receive_ + receiveCapacity_, 0);
  socket_.Close();
}
bool DeviceMediaSession::Fail(const char* code) {
  error_ = code; Stop();
  if (engine_) engine_->Fail();
  ClearInput();
  return false;
}
bool DeviceMediaSession::Open(const std::string& host, const TurnCorrelation& opening,
    const std::string& token, const std::string& key, bool live, const std::string& request) {
  if (opened_ || stopped_.load() || !engine_ || !receive_ || receiveCapacity_ < 65536 ||
      request.empty() || request.size() > 4096 || !CanonicalUuid(opening.conversationId) || !CanonicalUuid(key))
    return Fail("invalid_media_open");
  openedAt_ = now_();
  if (!socket_.Open(host, opening.conversationId, token, key) || stopped_.load() || now_() - openedAt_ >= 5000)
    return Fail("media_connect_failed");
  std::string wire = std::string("{\"type\":\"open\",\"engine\":\"") + (live ? "gpt-live" : "realtime") +
      "\",\"request\":" + request + "}";
  const bool sent = socket_.Send(false, reinterpret_cast<const uint8_t*>(wire.data()), wire.size());
  Clear(wire);
  if (!sent || stopped_.load()) return Fail("media_open_unknown");
  opened_ = true;
  return true;
}
bool DeviceMediaSession::Submit(const Command& command, Handle& handle) {
  handle = 0;
  if (stopped_.load() || !opened_ || nextHandle_ == UINT64_MAX || requestCount_ == requestIds_.size() ||
      (command.kind == Kind::Played && (!CanonicalUuid(command.operationId) ||
          command.operationId != operationId_ || command.responseGeneration != operationGeneration_)) ||
      (command.kind == Kind::Checkpoint && (!CanonicalUuid(command.playbackId) ||
          !command.pcmBytes || command.pcmBytes > 9120000 || command.pcmBytes % 2))) return false;
  for (auto& reply : replies_) {
    if (reply.handle) continue;
    const auto id = requestId_();
    if (!CanonicalUuid(id)) return false;
    // Request IDs are immutable once admitted and cannot alias a retained action.
    for (size_t index = 0; index != requestCount_; ++index) if (requestIds_[index] == id) return false;
    requestIds_[requestCount_++] = id;
    reply.id = id;
    reply.wire = command.kind == Kind::Played
        ? "{\"type\":\"played\",\"requestId\":\"" + id + "\",\"operationId\":\"" + command.operationId +
            "\",\"responseGeneration\":" + std::to_string(command.responseGeneration) + "}"
        : "{\"type\":\"playback\",\"requestId\":\"" + id + "\",\"playbackId\":\"" + command.playbackId +
            "\",\"pcmBytes\":" + std::to_string(command.pcmBytes) + ",\"drained\":true}";
    reply.handle = ++nextHandle_; handle = reply.handle;
    return true;
  }
  return false;
}
VoiceControlPort::Progress DeviceMediaSession::Poll(Handle handle) {
  if (!handle || stopped_.load()) return Progress::Failed;
  for (auto& reply : replies_) {
    if (reply.handle != handle || reply.abandoned) continue;
    if (!reply.complete) return Progress::Pending;
    const bool success = reply.success;
    Clear(reply.wire); Clear(reply.id); reply = {};
    return success ? Progress::Succeeded : Progress::Failed;
  }
  return Progress::Failed;
}
void DeviceMediaSession::Abandon(Handle handle) {
  for (auto& reply : replies_) {
    if (reply.handle != handle) continue;
    // A sent command still consumes protocol credit until its exact ACK arrives.
    // Its late durable effect must be observed, without restoring engine authority.
    if (reply.sent && !reply.complete) reply.abandoned = true;
    else { Clear(reply.wire); Clear(reply.id); reply = {}; }
    return;
  }
}
bool DeviceMediaSession::PumpControl() {
  if (waitingReply_) {
    for (const auto& reply : replies_)
      if (reply.handle == waitingReply_ && now_() - reply.sentAt >= 5000) return Fail("media_control_ack_unknown");
    return true;
  }
  Reply* next = nullptr;
  for (auto& reply : replies_)
    if (reply.handle && !reply.sent && (!next || reply.handle < next->handle)) next = &reply;
  if (!next) return true;
  next->sent = true; next->sentAt = now_(); waitingReply_ = next->handle;
  if (!socket_.Send(false, reinterpret_cast<const uint8_t*>(next->wire.data()), next->wire.size()))
    return Fail("media_control_send_unknown");
  return true;
}
void DeviceMediaSession::ClearInput() {
  std::fill(captured_.begin(), captured_.end(), 0); captured_.clear(); capturedOffset_ = 0;
  inputFrame_.fill(0);
}
bool DeviceMediaSession::PumpInput() {
  if (inputWaiting_) {
    if (now_() - inputSentAt_ >= 5000) return Fail("media_input_ack_unknown");
    return true;
  }
  if (!engine_->Uploading() || !inputStream_) { ClearInput(); return true; }
  if (!captured_.empty() && capturedEpoch_ != engine_->InputEpoch()) ClearInput();
  if (captured_.empty()) {
    capturedEpoch_ = engine_->InputEpoch();
    if (!capture_(captured_)) return Fail("audio_capture_failed");
    if (captured_.size() > 4800 || captured_.size() % 2) return Fail("invalid_media_input");
    if (capturedEpoch_ != engine_->InputEpoch()) { ClearInput(); return true; }
    if (captured_.empty()) return true;
  }
  const auto count = std::min<size_t>(4096, captured_.size() - capturedOffset_);
  if (inputSequence_ == UINT32_MAX || count > 8640000 - inputBytes_) return Fail("media_input_limit");
  size_t used = 0;
  if (!kufr_media::WriteInputPcm(inputStream_, inputSequence_, captured_.data() + capturedOffset_, count,
      inputFrame_.data(), inputFrame_.size(), used)) return Fail("invalid_media_input");
  // Publish the original immutable envelope before I/O; an ambiguous send is
  // terminal and is never reissued under a new sequence or listener identity.
  inputWaiting_ = true; waitingInputStream_ = inputStream_; waitingInputSequence_ = inputSequence_++;
  inputSentAt_ = now_(); inputBytes_ += count;
  const bool sent = socket_.Send(true, inputFrame_.data(), used);
  inputFrame_.fill(0);
  std::fill(captured_.begin() + capturedOffset_, captured_.begin() + capturedOffset_ + count, 0);
  capturedOffset_ += count;
  if (capturedOffset_ == captured_.size()) ClearInput();
  return sent || Fail("media_input_send_unknown");
}
bool DeviceMediaSession::Control(const kufr_media::Control& control) {
  using K = kufr_media::ControlKind;
  switch (control.kind) {
    case K::Input:
      if (control.stream <= highestStream_) return Fail("media_stream_reused");
      highestStream_ = inputStream_ = control.stream; inputId_ = control.inputId; inputSequence_ = 0;
      ClearInput(); return true;
    case K::Output:
      if (control.stream <= highestStream_ || control.generation != engine_->ResponseGeneration())
        return Fail("media_output_mismatch");
      highestStream_ = outputStream_ = control.stream; outputGeneration_ = control.generation; outputSequence_ = 0;
      return true;
    case K::Operation:
      if (control.generation != engine_->ResponseGeneration()) return Fail("media_operation_mismatch");
      operationId_ = control.operationId; operationGeneration_ = control.generation; return true;
    case K::InputAck:
      if (!inputWaiting_ || control.stream != waitingInputStream_ || control.sequence != waitingInputSequence_)
        return Fail("media_input_ack_mismatch");
      inputWaiting_ = false; return true;
    case K::ControlAck:
      for (auto& reply : replies_) {
        if (!reply.handle || reply.id != control.requestId) continue;
        if (!reply.sent || reply.complete || waitingReply_ != reply.handle) return Fail("media_control_ack_mismatch");
        waitingReply_ = 0; reply.complete = true; reply.success = control.status == 204;
        if (reply.abandoned) { Clear(reply.wire); Clear(reply.id); reply = {}; }
        return true;
      }
      return Fail("media_control_ack_mismatch");
  }
  return Fail("invalid_media_control");
}
bool DeviceMediaSession::Message(bool binary, const uint8_t* data, size_t length) {
  if (binary) {
    kufr_media::PcmFrame frame;
    if (!kufr_media::ReadOutputPcm(data, length, frame) || frame.stream != outputStream_ ||
        frame.sequence != outputSequence_ || outputSequence_ == UINT32_MAX)
      return Fail("media_output_sequence_mismatch");
    ++outputSequence_;
    return engine_->Audio(outputGeneration_, frame.pcm, frame.bytes) || Fail("audio_queue_failed");
  }
  kufr_media::Control control;
  if (kufr_media::ReadControl(reinterpret_cast<const char*>(data), length, control)) return Control(control);
  RealtimeEvent event;
  std::string text(reinterpret_cast<const char*>(data), length);
  const bool parsed = ParseRealtimeEvent(text, event);
  Clear(text);
  if (!parsed || event.type == "audio") return Fail("invalid_media_event"); // Binary is the sole PCM carrier.
  if (event.type == "listening" && event.inputId != inputId_) return Fail("media_input_binding_mismatch");
  if ((event.type == "started" || event.type == "completed") &&
      (event.operationId != operationId_ || event.responseGeneration != operationGeneration_))
    return Fail("media_operation_mismatch");
  const bool accepted = engine_->Event(event);
  Clear(event.delta); Clear(event.operation.reply);
  return accepted || Fail("media_event_refused");
}
bool DeviceMediaSession::Step() {
  if (!opened_ || stopped_.load() || !engine_ || now_() - openedAt_ >= 180000)
    return Fail("media_session_expired");
  if (!engine_->Poll() || !PumpControl() || !PumpInput()) return false;
  size_t used = 0; bool binary = false;
  const auto result = socket_.Receive(receive_, receiveCapacity_, used, binary);
  if (stopped_.load()) return false;
  if (result == DeviceMediaSocketPort::ReceiveResult::Idle) return true;
  if (result == DeviceMediaSocketPort::ReceiveResult::Closed)
    return engine_->Closed() && !engine_->HasPendingActions() ? true : Fail("media_connection_lost");
  if (result != DeviceMediaSocketPort::ReceiveResult::Message || !used || used >= receiveCapacity_ ||
      used > 20000000 - receivedBytes_)
    return Fail("invalid_media_message");
  receivedBytes_ += used;
  const bool valid = Message(binary, receive_, used);
  std::fill(receive_, receive_ + used, 0);
  return valid;
}

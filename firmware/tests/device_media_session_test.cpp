#include "device_media_fixture.h"
#include "../src/features/voice_turn/DeviceMediaFailure.h"

namespace {
void TerminalWireNeedsDrainAndTypedAck() {
  for (const bool live : {false, true}) {
    Fixture value(live, false); StartWire(value); AudioWire(value, 1, 0); CompletedWire(value, 0);
    for (unsigned index = 0; index != 3; ++index) assert(value.session->Step());
    assert(value.completed == 0 && value.socket.sent.size() == 1 && !value.engine->Closed());
    value.playback.ready = true; assert(value.session->Step());
    assert(value.socket.sent.size() == 2 && value.completed == 0);
    // Even an older peer's premature closed event cannot mint local completion.
    Json(value, "{\"type\":\"closed\",\"reason\":\"conversation_terminal\",\"responseGeneration\":0}");
    assert(value.completed == 0 && !value.engine->Closed());
    Json(value, "{\"type\":\"control.ack\",\"requestId\":\"" + Id + "\",\"status\":204}");
    assert(value.completed == 0 && value.session->Step());
    assert(value.completed == 1 && value.engine->Closed() && value.session->Step() && value.completed == 1);
  }
}
void CancelRetainsSentReceiptWithoutRevivingOldAction() {
  Fixture value(true, false); StartWire(value); AudioWire(value, 1, 0);
  Json(value, "{\"type\":\"playback_checkpoint\",\"playbackId\":\"" + Id +
      "\",\"pcmBytes\":4,\"decisionRevision\":1,\"responseGeneration\":0}");
  value.playback.ready = true; assert(value.session->Step() && value.socket.sent.size() == 2);
  Json(value, "{\"type\":\"audio_cancel\",\"responseGeneration\":1}");
  Json(value, "{\"type\":\"media.operation\",\"operationId\":\"" + Id + "\",\"responseGeneration\":1}");
  AudioWire(value, 2, 1); value.nextId = Other;
  CompletedWire(value, 1);
  assert(value.session->Step() && value.socket.sent.size() == 2 && value.completed == 0);
  Json(value, "{\"type\":\"control.ack\",\"requestId\":\"" + Id + "\",\"status\":204}");
  assert(value.session->Step() && value.socket.sent.size() == 3 && value.completed == 0);
  Json(value, "{\"type\":\"control.ack\",\"requestId\":\"" + Other + "\",\"status\":204}");
  assert(value.session->Step() && value.completed == 1);
}
void InputCreditAndUnknownResult() {
  Fixture value; value.Input(); value.capture.assign(4800, 7);
  assert(value.session->Step() && value.socket.sent.size() == 2);
  assert(value.socket.sent[1].binary && value.socket.sent[1].bytes.size() == 4112);
  for (unsigned index = 0; index != 10; ++index) assert(value.session->Step());
  assert(value.socket.sent.size() == 2); // No next frame before the exact ACK.
  value.socket.Json("{\"type\":\"media.ack\",\"stream\":1,\"sequence\":0}");
  assert(value.session->Step()); assert(value.session->Step());
  assert(value.socket.sent.size() == 3 && value.socket.sent[2].bytes.size() == 720);
  assert(value.socket.sent[2].bytes[8] == 1);
  value.now += 5000;
  assert(!value.session->Step() && value.playback.stopped && value.socket.sent.size() == 3);
}
void TypedControlCreditAndAbandonment() {
  Fixture value; const auto first = value.Checkpoint(); value.nextId = Other; const auto second = value.Checkpoint();
  assert(value.session->Step() && value.socket.sent.size() == 2);
  value.session->Abandon(first);
  for (unsigned index = 0; index != 3; ++index) assert(value.session->Step());
  assert(value.socket.sent.size() == 2 && value.session->Poll(second) == VoiceControlPort::Progress::Pending);
  value.socket.Json("{\"type\":\"control.ack\",\"requestId\":\"" + Id + "\",\"status\":204}");
  assert(value.session->Step() && value.session->Step());
  assert(value.socket.sent.size() == 3 && value.session->Poll(first) == VoiceControlPort::Progress::Failed);
  value.socket.Json("{\"type\":\"control.ack\",\"requestId\":\"" + Other + "\",\"status\":204}");
  assert(value.session->Step() && value.session->Poll(second) == VoiceControlPort::Progress::Succeeded);
  assert(value.session->Poll(second) == VoiceControlPort::Progress::Failed);
  VoiceControlPort::Handle reused = 0;
  VoiceControlPort::Command command; command.kind = VoiceControlPort::Kind::Checkpoint; command.playbackId = Id; command.pcmBytes = 6;
  assert(!value.session->Submit(command, reused)); // Even a consumed request ID remains retired.
}
void UnknownSendAndUnsolicitedReceiptFailClosed() {
  {
    Fixture value; value.Checkpoint(); value.socket.failSend = true;
    assert(!value.session->Step() && value.playback.stopped);
    assert(value.socket.sent.size() == 2 && !value.session->Step() && value.socket.sent.size() == 2);
  }
  {
    Fixture value;
    value.socket.Json("{\"type\":\"control.ack\",\"requestId\":\"" + Id + "\",\"status\":204}");
    assert(!value.session->Step() && value.playback.stopped);
  }
  {
    Fixture value; value.Input(); value.session->Stop(); value.capture.assign(960, 1);
    assert(!value.session->Step() && value.socket.sent.size() == 1);
  }
}
}
int main() {
  assert(std::strcmp(PublicDeviceMediaError("audio_capture_failed"), "audio_failed") == 0);
  assert(std::strcmp(PublicDeviceMediaError("media_capacity_unavailable"), "audio_failed") == 0);
  assert(std::strcmp(PublicDeviceMediaError("media_control_ack_unknown"), "ai_unavailable") == 0);
  InputCreditAndUnknownResult(); TypedControlCreditAndAbandonment(); UnknownSendAndUnsolicitedReceiptFailClosed();
  TerminalWireNeedsDrainAndTypedAck(); CancelRetainsSentReceiptWithoutRevivingOldAction();
  std::cout << "Actual shared engine/device socket actor: immutable input, bounded credits, typed ACKs and unknown outcomes passed.\n";
}

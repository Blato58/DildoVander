#include <cassert>
#include <iostream>
#include <map>
#include <memory>
#include "../src/features/voice_turn/RealtimeSessionEngine.h"
#include "../src/features/voice_turn/BoundedVoicePlayback.h"

namespace {
struct LegacyAudio final : VoiceAudioPort {
  bool Available() const override { return true; }
  void Listen(bool) override { assert(false); }
  bool TakeInput(std::vector<uint8_t>&) override { return false; }
  bool Play(const std::vector<uint8_t>&) override { assert(false); return false; }
  bool Drain() override { assert(false); return false; }
  bool CancelPlayback() override { assert(false); return false; }
  void Stop() override { assert(false); }
  bool Failed() const override { return false; }
};

struct Playback final : VoicePlaybackPort {
  uint64_t generation = 0, next = 0;
  size_t bytes = 0, queued = 0;
  bool cancelReady = false, stopped = false, failed = false, reuse = false;
  std::map<DrainHandle, size_t> frontier;
  std::map<DrainHandle, Progress> results;
  std::map<DrainHandle, bool> abandoned;
  bool Queue(uint64_t value, const uint8_t*, size_t length) override {
    assert(!stopped && value == generation);
    bytes += length; ++queued; return true;
  }
  Progress Drain(uint64_t) override { assert(false); return Progress::Failed; }
  bool RequestDrain(uint64_t value, DrainHandle& handle) override {
    assert(value == generation);
    handle = reuse ? next : ++next; frontier[handle] = bytes; results[handle] = Progress::Pending; return true;
  }
  Progress PollDrain(DrainHandle handle) override { assert(!abandoned[handle]); return results.at(handle); }
  void AbandonDrain(DrainHandle handle) override { abandoned[handle] = true; }
  Progress Cancel(uint64_t value) override {
    if (generation != value) { generation = value; bytes = 0; }
    return cancelReady ? Progress::Succeeded : Progress::Pending;
  }
  bool Failed() const override { return failed; }
  void Stop() override { stopped = true; }
};

struct Control final : VoiceControlPort {
  Handle next = 0;
  bool reject = false, stopped = false, reuse = false;
  std::vector<Command> commands;
  std::map<Handle, Progress> results;
  std::map<Handle, bool> abandoned;
  bool Submit(const Command& command, Handle& handle) override {
    if (reject) return false;
    assert(!stopped);
    handle = reuse ? next : ++next; commands.push_back(command); results[handle] = Progress::Pending; return true;
  }
  Progress Poll(Handle handle) override { assert(!abandoned[handle]); return results.at(handle); }
  void Abandon(Handle handle) override { abandoned[handle] = true; }
  void Stop() override { stopped = true; }
};

struct Fixture {
  LegacyAudio audio;
  Playback playback;
  Control control;
  TurnCorrelation correlation;
  ConversationRuntimeSnapshot snapshot;
  uint64_t now = 100, utc = 1767225600;
  bool current = true, capture = false;
  unsigned completed = 0, posts = 0;
  std::unique_ptr<RealtimeSessionEngine> engine;
  explicit Fixture(VoicePlaybackPort* externalPlayback = nullptr) {
    correlation.deviceId = "device"; correlation.bootId = "boot";
    correlation.conversationId = "conversation"; correlation.clientTurnId = "input";
    correlation.generation = 4; correlation.deviceRevision = 37; correlation.operationEpoch = UINT64_MAX;
    snapshot.generation = 4; snapshot.deviceRevision = 37; snapshot.readyMask = 63;
    snapshot.catalogVersion = "catalog"; snapshot.operationEpoch = UINT64_MAX;
    engine.reset(new RealtimeSessionEngine(audio, true, [&] { return current; }, [&] { return now; },
        [&] { return utc; }, [](const char*) {},
        [&](const std::string&, const void*, size_t, const char*, const std::string&, unsigned) { ++posts; return 204; },
        correlation, snapshot, [&](const TurnOperationObservation&) { ++completed; return true; },
        {externalPlayback ? *externalPlayback : playback, control, [&](bool value) { capture = value; }}));
    auto session = Event("session"); session.streamId = "stream"; assert(engine->Event(session));
    auto started = Event("started"); started.operationId = "operation"; started.operation = Operation();
    assert(engine->Event(started));
  }
  RealtimeEvent Event(const char* type, uint64_t generation = 0) const {
    RealtimeEvent event; event.type = type; event.hasResponseGeneration = true; event.responseGeneration = generation;
    return event;
  }
  TurnOperationObservation Operation(bool proposal = false) const {
    TurnOperationObservation result;
    result.operationId = "operation"; result.kind = "turn"; result.status = TurnOperationStatus::Completed;
    result.correlation = correlation; result.reply = "fixture reply"; result.decision = proposal ? "propose" : "no_selection";
    result.hasProposal = proposal;
    if (proposal) {
      auto& value = result.proposal;
      value.proposalId = "proposal"; value.conversationId = correlation.conversationId;
      value.turnId = correlation.clientTurnId; value.catalogVersion = snapshot.catalogVersion;
      value.bootId = correlation.bootId; value.generation = correlation.generation;
      value.deviceRevision = correlation.deviceRevision; value.compartment = 2;
      value.operationEpoch = correlation.operationEpoch; value.expiresAt = "2026-01-01T00:00:10Z";
    }
    return result;
  }
  RealtimeEvent Completed(bool proposal = false, uint64_t generation = 0) const {
    auto event = Event("completed", generation); event.operationId = "operation"; event.operation = Operation(proposal); return event;
  }
  void Audio(uint64_t generation = 0, size_t length = 6) {
    std::vector<uint8_t> pcm(length, 0x5A); assert(engine->Audio(generation, pcm.data(), pcm.size()));
  }
};

void CheckpointRetainsOriginalFrontier() {
  Fixture value; value.Audio();
  auto checkpoint = value.Event("playback_checkpoint"); checkpoint.playbackId = "checkpoint"; checkpoint.pcmBytes = 6;
  assert(value.engine->Event(checkpoint));
  assert(value.playback.frontier.at(1) == 6);
  value.Audio(0, 4);
  assert(value.playback.frontier.at(1) == 6 && value.engine->Poll() && value.control.commands.empty());
  value.playback.results[1] = VoicePlaybackPort::Progress::Succeeded;
  assert(value.engine->Poll() && value.control.commands.size() == 1 && value.completed == 0);
  const auto command = value.control.commands[0];
  assert(command.kind == VoiceControlPort::Kind::Checkpoint && command.playbackId == "checkpoint"
      && command.operationId == "operation" && command.pcmBytes == 6 && command.responseGeneration == 0);
  assert(value.engine->Poll() && value.engine->HasPendingActions());
  value.control.results[1] = VoiceControlPort::Progress::Succeeded;
  assert(value.engine->Poll() && !value.engine->HasPendingActions() && value.completed == 0 && value.posts == 0);
}

void CompletionRequiresExactAck() {
  Fixture value; value.Audio();
  assert(value.engine->Event(value.Completed()));
  assert(value.engine->Event(value.Event("closed")) && !value.engine->Closed());
  assert(value.engine->Poll() && value.completed == 0);
  value.playback.results[1] = VoicePlaybackPort::Progress::Succeeded;
  assert(value.engine->Poll() && value.control.commands[0].kind == VoiceControlPort::Kind::Played && value.completed == 0);
  value.control.results[99] = VoiceControlPort::Progress::Succeeded;
  assert(value.engine->Poll() && value.completed == 0 && !value.engine->Closed());
  value.control.results[1] = VoiceControlPort::Progress::Succeeded;
  assert(value.engine->Poll() && value.completed == 1 && value.engine->Closed());
  assert(value.engine->Poll() && value.completed == 1 && value.posts == 0);
}

void ProposalExpiryIsFreshAtBothBoundaries() {
  for (bool expireAfterAck : {false, true}) {
    Fixture value; value.Audio(); assert(value.engine->Event(value.Completed(true)));
    value.playback.results[1] = VoicePlaybackPort::Progress::Succeeded;
    if (expireAfterAck) {
      assert(value.engine->Poll() && value.control.commands.size() == 1);
      value.control.results[1] = VoiceControlPort::Progress::Succeeded;
    }
    value.utc += 11;
    assert(!value.engine->Poll() && value.engine->Failed() && value.completed == 0);
    assert(expireAfterAck || value.control.commands.empty());
  }
}

void CancelDoesNotWaitForOldProofs() {
  for (bool waitingForAck : {false, true}) {
    Fixture value; value.Audio(); assert(value.engine->Event(value.Completed()));
    if (waitingForAck) {
      value.playback.results[1] = VoicePlaybackPort::Progress::Succeeded;
      assert(value.engine->Poll() && value.control.commands.size() == 1);
    }
    assert(value.engine->Event(value.Event("audio_cancel", 1)));
    assert(value.playback.abandoned[1] && (!waitingForAck || value.control.abandoned[1]));
    value.Audio(1);
    value.control.results[1] = VoiceControlPort::Progress::Succeeded;
    value.playback.results[1] = VoicePlaybackPort::Progress::Failed;
    assert(value.engine->Poll() && value.completed == 0 && value.playback.queued == 2);
    value.playback.cancelReady = true;
    assert(value.engine->Poll() && !value.engine->HasPendingActions());
    value.Audio(1);
    assert(value.engine->Event(value.Completed(false, 1)));
    value.playback.results[2] = VoicePlaybackPort::Progress::Succeeded;
    assert(value.engine->Poll() && value.control.commands.back().responseGeneration == 1);
    const auto exactHandle = value.control.next;
    assert(value.control.commands.size() == (waitingForAck ? 2u : 1u));
    value.control.results[exactHandle] = VoiceControlPort::Progress::Succeeded;
    assert(value.engine->Poll() && value.completed == 1);
  }
}

void BoundsAndFailuresCloseOriginalSession() {
  {
    Fixture value; value.Audio();
    for (int index = 0; index < 4; ++index) assert(value.engine->Event(value.Event("pause")));
    assert(!value.engine->Event(value.Event("pause")) && value.engine->Failed() && value.playback.stopped && value.control.stopped);
  }
  for (int failure = 0; failure < 4; ++failure) {
    Fixture value; value.Audio(); assert(value.engine->Event(value.Completed()));
    value.playback.results[1] = VoicePlaybackPort::Progress::Succeeded;
    if (failure == 0) value.control.reject = true;
    if (failure == 1) value.now += 35000;
    if (failure == 2) value.current = false;
    if (failure == 3) {
      assert(value.engine->Poll()); value.control.results[1] = VoiceControlPort::Progress::Failed;
    }
    assert(!value.engine->Poll() && value.completed == 0 && value.control.stopped && value.playback.stopped);
  }
  {
    Fixture value; uint8_t pcm[4] = {};
    assert(!value.engine->Audio(0, pcm, 3) && !value.engine->Audio(0, pcm, 32770));
    assert(value.playback.queued == 0);
    value.playback.failed = true;
    assert(!value.engine->Current());
  }
}

void DomainAndStopFencesRemainOriginal() {
  {
    Fixture value; value.Audio();
    auto wrongEpoch = value.Completed(); wrongEpoch.operation.correlation.operationEpoch = 0;
    assert(!value.engine->Event(wrongEpoch) && value.playback.next == 0);
    auto wrongSnapshot = value.Completed(true); wrongSnapshot.operation.proposal.deviceRevision = 38;
    assert(!value.engine->Event(wrongSnapshot) && value.playback.next == 0);
    auto wrongOperation = value.Completed(); wrongOperation.operation.operationId = "other-operation";
    assert(!value.engine->Event(wrongOperation) && value.playback.next == 0);
  }
  {
    Fixture value; value.Audio(); assert(value.engine->Event(value.Completed()));
    value.playback.results[1] = VoicePlaybackPort::Progress::Succeeded;
    assert(value.engine->Poll());
    value.control.results[1] = VoiceControlPort::Progress::Succeeded;
    value.current = false;
    assert(!value.engine->Poll() && value.completed == 0); // Fresh snapshot fence after ACK.
  }
  {
    Fixture value;
    auto listening = value.Event("listening"); listening.inputId = "input";
    assert(value.engine->Event(listening) && value.capture);
    value.Audio(); assert(value.engine->Event(value.Completed()));
    value.engine->Stop();
    value.playback.results[1] = VoicePlaybackPort::Progress::Succeeded;
    assert(!value.capture && value.playback.stopped && value.control.stopped
        && !value.engine->Poll() && value.completed == 0);
  }
  {
    Fixture value; value.Audio(); assert(value.engine->Event(value.Event("pause")));
    value.now += 34900;
    auto listening = value.Event("listening"); listening.inputId = "input";
    assert(value.engine->Event(listening));
    value.now += 100;
    assert(!value.engine->Poll()); // A newer input cannot extend an older barrier deadline.
  }
}

void ReusedPortReceiptsCannotGainAuthority() {
  for (bool reuseControl : {false, true}) {
    Fixture value; value.Audio();
    auto checkpoint = value.Event("playback_checkpoint"); checkpoint.playbackId = "checkpoint"; checkpoint.pcmBytes = 6;
    assert(value.engine->Event(checkpoint));
    value.playback.results[1] = VoicePlaybackPort::Progress::Succeeded;
    assert(value.engine->Poll());
    value.control.results[1] = VoiceControlPort::Progress::Succeeded;
    assert(value.engine->Poll() && !value.engine->HasPendingActions());
    if (reuseControl) {
      value.control.reuse = true;
      assert(value.engine->Event(value.Completed()));
      value.playback.results[2] = VoicePlaybackPort::Progress::Succeeded;
      assert(!value.engine->Poll());
    } else {
      value.playback.reuse = true;
      assert(!value.engine->Event(value.Completed()));
    }
    assert(value.engine->Failed() && value.completed == 0 && value.playback.stopped && value.control.stopped);
  }
}

struct RawPlayback final : BoundedPlaybackOwner::Port {
  bool active = false;
  unsigned plays = 0, drains = 0, cancels = 0;
  bool Begin(const BoundedPlaybackOwner::Fence& fence) override { active = fence.Current(); return active; }
  bool Play(const BoundedPlaybackOwner::Fence& fence, const uint8_t*, size_t) override {
    assert(active && fence.Current()); ++plays; return true;
  }
  bool Drain(const BoundedPlaybackOwner::Fence& fence) override { assert(fence.Current()); ++drains; return true; }
  bool CancelPlayback(const BoundedPlaybackOwner::Fence& fence) override { assert(fence.Current()); ++cancels; return true; }
  static void Stop(void* context) { static_cast<RawPlayback*>(context)->active = false; }
  static bool Mute(void*) { return true; }
};

void ActualOwnerFrontiersAndPendingAckCancellation() {
  RawPlayback raw;
  std::array<uint8_t, BoundedPlaybackOwner::WorkspaceBytes> workspace{};
  BoundedPlaybackOwner owner(raw, workspace.data(), workspace.size(), RawPlayback::Stop, RawPlayback::Mute, &raw);
  BoundedVoicePlayback playback(owner, 1);
  assert(playback.Begin() == VoicePlaybackPort::Progress::Pending && owner.RunOne()
      && playback.Begin() == VoicePlaybackPort::Progress::Succeeded);
  Fixture value(&playback);
  value.Audio(); assert(value.engine->Event(value.Event("pause")));
  value.Audio(0, 4);
  auto checkpoint = value.Event("playback_checkpoint"); checkpoint.playbackId = "checkpoint"; checkpoint.pcmBytes = 10;
  assert(value.engine->Event(checkpoint));
  value.Audio(0, 2);
  assert(owner.RunOne() && raw.plays == 1);
  assert(owner.RunOne() && raw.drains == 1 && !owner.RunOne());
  assert(value.engine->Poll() && value.control.commands.empty());
  assert(owner.RunOne() && raw.plays == 2);
  assert(owner.RunOne() && raw.drains == 2 && !owner.RunOne());
  assert(value.engine->Poll() && value.control.commands.size() == 1
      && value.control.commands[0].pcmBytes == 10 && value.completed == 0);
  assert(owner.RunOne() && raw.plays == 3); // Only after original checkpoint proof was consumed.
  assert(value.engine->Poll() && value.engine->HasPendingActions());
  assert(value.engine->Event(value.Event("audio_cancel", 1)) && value.control.abandoned[1]);
  value.Audio(1, 2); // RAM admission remains live while the physical cancel is pending.
  assert(owner.RunOne() && raw.cancels == 1 && raw.plays == 3);
  assert(owner.RunOne() && raw.plays == 4);
  value.control.results[1] = VoiceControlPort::Progress::Succeeded;
  assert(value.engine->Poll() && !value.engine->HasPendingActions() && value.completed == 0);
  value.engine->Stop();
}
void NoContinuationAfterPendingCompletion() {
  for (bool pendingAck : {false, true}) {
    for (const auto* continuation : {"audio", "pause", "playback_checkpoint"}) {
      Fixture value; value.Audio();
      assert(value.engine->Event(value.Completed(true)));
      if (pendingAck) {
        value.playback.results[1] = VoicePlaybackPort::Progress::Succeeded;
        assert(value.engine->Poll() && value.control.commands.size() == 1);
      }
      if (std::string(continuation) == "audio") {
        const uint8_t pcm[] = {1, 2};
        assert(!value.engine->Audio(0, pcm, sizeof(pcm)));
      } else {
        auto event = value.Event(continuation);
        event.playbackId = "checkpoint"; event.pcmBytes = 6;
        assert(!value.engine->Event(event));
      }
      value.playback.results[1] = VoicePlaybackPort::Progress::Succeeded;
      value.control.results[1] = VoiceControlPort::Progress::Succeeded;
      assert(!value.engine->Poll() && value.engine->Failed() && value.completed == 0);
      assert(value.playback.stopped && value.control.stopped);
    }
  }
}
}

int main() {
  CheckpointRetainsOriginalFrontier(); CompletionRequiresExactAck();
  ProposalExpiryIsFreshAtBothBoundaries(); CancelDoesNotWaitForOldProofs(); BoundsAndFailuresCloseOriginalSession();
  DomainAndStopFencesRemainOriginal();
  ReusedPortReceiptsCannotGainAuthority();
  ActualOwnerFrontiersAndPendingAckCancellation();
  NoContinuationAfterPendingCompletion();
  std::cout << "Async engine exact drain/control receipts, cancellation, fresh proposal TTL and bounded metadata passed\n";
}

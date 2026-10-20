#include "DeviceShowRuntime.h"

uint64_t DeviceShowRuntime::Fingerprint(const DeviceShowCue& cue) {
  uint64_t hash = 14695981039346656037ull;
  const std::string values = cue.duelId + '|' + cue.sceneId + '|' + cue.audioPath + '|' + cue.speechText + '|' +
      cue.cueType + '|' + cue.preset + '|' + cue.clipId + '|' + cue.voice + '|' +
      std::to_string(cue.round) + '|' + std::to_string(cue.compartment) + '|' + std::to_string(cue.generation) + '|' + std::to_string(cue.deviceRevision);
  for (unsigned char value : values) { hash ^= value; hash *= 1099511628211ull; }
  return hash;
}

void DeviceShowRuntime::Finish(DeviceShowMailbox::View& view, const char* outcome,
    const char* evidence, const char* reason) {
  scene_.CancelResponse(view.cue.cueId, view.cue.generation);
  if (ownsReservation_) runtime_.ReleaseDecoration(ownedGeneration_);
  ownsReservation_ = false;
  // The mailbox retains the final allocation for release by the network task.
  // No PSRAM wipe/free or HTTP work runs on the control loop.
  view.audio.reset(); playingAudio_.reset(); clip_ = {};
  mailbox_.Finish(outcome, evidence, reason);
  phase_ = Phase::Idle;
}

void DeviceShowRuntime::Tick(uint64_t nowMs, bool physicalAvailable, bool audioAvailable) {
  DeviceShowSettings settings;
  if (mailbox_.PeekSettings(settings)) {
    if (settings.stopRevision > observedStopRevision_ && settings.stopRevision > offline_.ServerStopRevision()) {
      observedStopRevision_ = settings.stopRevision;
      remoteStop_ = true; mailbox_.Cancel();
    }
    if (settings.stopRevision > offline_.ServerStopRevision()) offline_.ApplyServerStop(settings.stopRevision);
    const bool stopApplied = settings.stopRevision <= offline_.ServerStopRevision();
    if (stopApplied && offline_.ApplyServerSettings(settings.revision, settings.quiet, settings.ambient) && settings.quiet)
      mailbox_.Cancel();
    if (stopApplied && settings.revision <= offline_.ServerSettingsRevision()) mailbox_.ConfirmSettings(settings);
  }
  DeviceShowMailbox::View view = mailbox_.Read();
  if (view.stage == DeviceShowMailbox::Stage::Empty) {
    phase_ = Phase::Idle;
  } else if (phase_ == Phase::Idle) {
    ConversationRuntimeSnapshot snapshot;
    if (!physicalAvailable || runtime_.OfflineAuthority() || !offline_.PrepareInteraction() ||
        !state_.Read(snapshot) || view.cue.bootId != bootId_ || view.cue.generation != snapshot.generation ||
        view.cue.deviceRevision != snapshot.deviceRevision || nowMs < view.cue.issuedAtMs || nowMs >= view.cue.validUntilMs ||
        view.cue.validUntilMs - view.cue.issuedAtMs > 2000 || mailbox_.IsCancelled(view.cue.cueId)) {
      Finish(view, "rejected", "commanded", "stale_busy_or_cancelled");
    } else {
      const uint64_t fingerprint = Fingerprint(view.cue);
      for (const auto& previous : seen_) if (previous.id == view.cue.cueId) {
        Finish(view, "rejected", "commanded", previous.fingerprint == fingerprint ? "duplicate" : "id_conflict");
        return;
      }
      const bool needsAudio = view.cue.cueType == "PlayLocalClip" || !view.cue.audioPath.empty();
      if (needsAudio && !audioAvailable) {
        Finish(view, "rejected", "commanded", "audio_busy"); return;
      }
      if (!view.cue.audioPath.empty() && (!view.audio || !audio_.Available() || view.cue.voice != voice_)) {
        Finish(view, "rejected", "missing_assets", "audio_or_psram_unavailable");
        return;
      }
      if (view.cue.cueType == "PlayLocalClip") {
        const auto* approved = offline_.ApprovedClip(view.cue.clipId);
        if (!approved || !runtime_.ReserveDecoration(view.cue.generation)) {
          Finish(view, "rejected", "missing_assets", "approved_clip_unavailable"); return;
        }
        ownedGeneration_ = view.cue.generation; ownsReservation_ = true;
        clip_ = *approved;
        if (!mailbox_.Accept(nowMs) || !audio_.Play(clip_, view.cue.generation)) {
          Finish(view, "rejected", "missing_assets", "clip_player_unavailable"); return;
        }
        seen_[seenCursor_++ % seen_.size()] = {view.cue.cueId, nowMs, fingerprint};
        phase_ = Phase::Playback;
        return;
      }
      SceneIntentContext context;
      context.bootId = bootId_; context.sessionId = view.cue.duelId.empty() ? view.cue.cueId : view.cue.duelId;
      context.responseId = view.cue.cueId; context.sessionEpoch = view.cue.generation;
      context.responseGeneration = view.cue.generation; context.phaseRevision = view.cue.round;
      context.deviceGeneration = view.cue.generation; context.validUntilDeviceMs = view.cue.validUntilMs;
      context.compartment = view.cue.compartment;
      // Non-audio decorative reservations still exclude motors/manual owners,
      // but must not invalidate the active conversation merely by lighting LED.
      context.preserveConversationSnapshot = !needsAudio;
      scene_.SetContext(context);
      SceneIntent intent;
      intent.intentId = view.cue.cueId; intent.bootId = bootId_; intent.sessionId = context.sessionId;
      intent.responseId = context.responseId; intent.sessionEpoch = context.sessionEpoch;
      intent.responseGeneration = context.responseGeneration; intent.phaseRevision = context.phaseRevision;
      intent.issuedAtDeviceMs = view.cue.issuedAtMs; intent.validUntilDeviceMs = view.cue.validUntilMs;
      intent.compartment = view.cue.compartment;
      const std::string sceneId = view.cue.cueType == "LedPreset" ? view.cue.preset : view.cue.sceneId;
      intent.kind = view.cue.cueType == "VibratePreset" ? SceneIntentKind::Vibrate :
          sceneId == "ambient" ? SceneIntentKind::Ambient : sceneId == "wake" ? SceneIntentKind::Wake :
          sceneId == "farewell" ? SceneIntentKind::Farewell : SceneIntentKind::Consider;
      if (scene_.Accept(intent, nowMs) != SceneIntentResult::Started || !mailbox_.Accept(nowMs)) {
        Finish(view, "rejected", "commanded", "scene_unavailable");
        return;
      }
      seen_[seenCursor_++ % seen_.size()] = {view.cue.cueId, nowMs, fingerprint};
      ownedGeneration_ = view.cue.generation;
      ownsReservation_ = true;
      phase_ = Phase::Led;
    }
  } else {
    const bool needsAudio = view.cue.cueType == "PlayLocalClip" || !view.cue.audioPath.empty();
    const bool cancelled = !physicalAvailable || (needsAudio && !audioAvailable) || runtime_.Generation() != view.cue.generation ||
        nowMs >= view.executionDeadlineMs || mailbox_.IsCancelled(view.cue.cueId);
    if (cancelled && phase_ != Phase::Cancelling) {
      if (phase_ == Phase::Playback) audio_.Cancel();
      scene_.CancelResponse(view.cue.cueId, view.cue.generation);
      phase_ = Phase::Cancelling;
    }
    if (phase_ == Phase::Cancelling) {
      if (!audio_.Busy()) Finish(view, "cancelled", "commanded", "preempted_or_expired");
    } else if (phase_ == Phase::Led && !runtime_.HasDecoration()) {
      if (view.cue.audioPath.empty()) Finish(view, "completed", "commanded", "silent_led_cue");
      else if (runtime_.ReserveDecoration(view.cue.generation)) phase_ = Phase::AwaitAudio;
      else Finish(view, "cancelled", "commanded", "reservation_lost");
    } else if (phase_ == Phase::AwaitAudio && view.stage == DeviceShowMailbox::Stage::AudioFinished) {
      if (!view.audioSucceeded || !view.audio || !view.audio->Pcm()) {
        Finish(view, "rejected", "missing_assets", "audio_download_failed");
      } else {
        playingAudio_ = view.audio;
        clip_ = {"device-show", "", playingAudio_->Pcm(), playingAudio_->PcmBytes(),
            static_cast<uint32_t>(playingAudio_->PcmBytes() * 1000 / 64000), "", view.cue.compartment,
            kufr::offline::ClipFormat::PcmS16Le32k, static_cast<uint32_t>(playingAudio_->PcmBytes() / 2)};
        if (audio_.Play(clip_, view.cue.generation)) phase_ = Phase::Playback;
        else Finish(view, "rejected", "missing_assets", "audio_player_unavailable");
      }
    } else if (phase_ == Phase::Playback && !audio_.Busy()) {
      Finish(view, audio_.Failed() ? "rejected" : "completed",
          audio_.Failed() ? "missing_assets" : "playback_finished",
          audio_.Failed() ? "playback_failed" : "");
    }
  }
  const DeviceShowSettings applied{offline_.ServerSettingsRevision(), offline_.QuietEnabled(), offline_.AmbientEnabled(), offline_.ServerStopRevision()};
  mailbox_.PublishState(physicalAvailable && phase_ == Phase::Idle && !runtime_.OfflineAuthority(), applied,
      runtime_.Generation(), runtime_.CurrentRevision());
}

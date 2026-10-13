#pragma once
#include <atomic>
#include "VoiceAudioPort.h"

// Called under the backend voice mutex. Keep queued voice busy until its audio
// gate has closed; an inactive voice must leave SD playback/cues to their owner.
inline void CancelVoiceOutput(std::atomic<uint64_t>& epoch,
                              std::atomic<bool>& pending,
                              std::atomic<bool>& busy, VoiceAudioPort& audio) {
  epoch.fetch_add(1);
  const bool queued = pending.exchange(false);
  if (busy.load() || queued) audio.Stop();
  if (queued) busy.store(false);
}

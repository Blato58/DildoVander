#pragma once
#include <stdint.h>

// Optional local sound. A busy/unverified audio path may decline a cue; it must
// never delay the control owner or initiate a network/AI operation.
class PresentationAudioPort {
 public:
  enum class CueResult { Requested, Unavailable, Unverified };
  virtual ~PresentationAudioPort() = default;
  virtual void Cue(uint16_t frequencyHz, uint16_t durationMs) = 0;
  virtual void StopCues() = 0;
  // Legacy void ports cannot prove acceptance or playback. New adapters may
  // report refusal without taking audio from its current owner.
  virtual CueResult TryCue(uint16_t frequencyHz, uint16_t durationMs) {
    Cue(frequencyHz, durationMs);
    return CueResult::Unverified;
  }
};

#pragma once
#include <stdint.h>

// Optional local sound. A busy/unverified audio path may decline a cue; it must
// never delay the control owner or initiate a network/AI operation.
class PresentationAudioPort {
 public:
  virtual ~PresentationAudioPort() = default;
  virtual void Cue(uint16_t frequencyHz, uint16_t durationMs) = 0;
  virtual void StopCues() = 0;
};

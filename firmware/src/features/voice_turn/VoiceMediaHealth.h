#pragma once
#include <cstdint>

// Redacted numeric observations only. Queues/frontiers are software facts;
// acoustic cancellation tail and DMA drain latency require board measurement.
struct VoiceMediaHealth {
  uint64_t session = 0, cancellationEpoch = 0, playedPcmBytes = 0;
  uint32_t captureQueuedBytes = 0, captureHighWaterBytes = 0, captureOverruns = 0;
  uint32_t playbackQueuedBytes = 0, playbackHighWaterBytes = 0;
  uint32_t termination = 0; // 0 idle, 1 active, 2 completed, 3 cancelled, 4 failed.
};

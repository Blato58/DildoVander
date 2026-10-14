#pragma once
using TaskHandle_t=void*;
// Host tests may run bounded iterations of the actual capture entry point.
// Hardware DMA remains a separate acceptance gate.
struct AudioCaptureTask { void (*run)(void*) = nullptr; void* context = nullptr; };
inline AudioCaptureTask& CapturedAudioTask() { static AudioCaptureTask task; return task; }
inline int xTaskCreate(void (*run)(void*), const char*, unsigned, void* context, unsigned, TaskHandle_t* task) {
 CapturedAudioTask() = {run, context}; *task=reinterpret_cast<void*>(1); return 1;
}

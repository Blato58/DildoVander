#pragma once
using TaskHandle_t=void*;
// Capture is not run by this driver test; hardware DMA stays an HW acceptance.
inline int xTaskCreate(void (*)(void*), const char*, unsigned, void*, unsigned, TaskHandle_t* task) { *task=reinterpret_cast<void*>(1); return 1; }

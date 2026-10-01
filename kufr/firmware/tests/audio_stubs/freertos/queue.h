#pragma once
#include <cstddef>
using QueueHandle_t=void*;
inline QueueHandle_t xQueueCreate(unsigned, size_t) { return reinterpret_cast<void*>(1); }
inline int xQueueReset(QueueHandle_t) { return 1; }
inline int xQueueReceive(QueueHandle_t, void*, unsigned) { return 0; }
inline int xQueueSend(QueueHandle_t, const void*, unsigned) { return 1; }

#pragma once
#include <cstdint>
#include <mutex>
constexpr int pdPASS=1, pdTRUE=1;
#define pdMS_TO_TICKS(value) (value)
using portMUX_TYPE = std::mutex;
#define portMUX_INITIALIZER_UNLOCKED {}
#define portENTER_CRITICAL(mutex) (mutex)->lock()
#define portEXIT_CRITICAL(mutex) (mutex)->unlock()

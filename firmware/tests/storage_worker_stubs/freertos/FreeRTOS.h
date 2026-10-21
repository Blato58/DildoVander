#pragma once

#include <cstdint>

using BaseType_t = int;
using UBaseType_t = unsigned;
using TickType_t = uint32_t;

constexpr BaseType_t pdFALSE = 0;
constexpr BaseType_t pdTRUE = 1;
constexpr BaseType_t pdFAIL = 0;
constexpr BaseType_t pdPASS = 1;
constexpr TickType_t portMAX_DELAY = UINT32_MAX;

// Deliberately model a 100 Hz host kernel: two milliseconds rounds to zero.
#define pdMS_TO_TICKS(milliseconds) static_cast<TickType_t>((static_cast<uint64_t>(milliseconds) * 100u) / 1000u)

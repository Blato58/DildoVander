#pragma once
#include "../sd_stubs/SdPlatform.h"
namespace dance_fake { extern uint32_t random; }
inline uint32_t esp_random() { return dance_fake::random; }

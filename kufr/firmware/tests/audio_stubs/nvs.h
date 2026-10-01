#pragma once
#include <cstdint>
using nvs_handle_t=unsigned;
constexpr int NVS_READONLY=0;
int nvs_open(const char*, int, nvs_handle_t*);
int nvs_get_u8(nvs_handle_t, const char*, uint8_t*);
void nvs_close(nvs_handle_t);

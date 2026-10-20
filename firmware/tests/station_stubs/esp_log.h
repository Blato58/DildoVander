#pragma once
inline void StubLog(const char*, const char*) {}
#define ESP_LOGE(tag, message) StubLog(tag, message)

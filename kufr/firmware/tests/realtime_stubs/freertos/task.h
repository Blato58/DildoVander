#pragma once
#include <thread>
#include <chrono>
using TaskHandle_t = void*;
inline int xTaskCreate(void (*function)(void*), const char*, unsigned, void* arg, unsigned, TaskHandle_t* task) {
  *task = reinterpret_cast<void*>(1);
  std::thread([=] { function(arg); }).detach(); return 1;
}
inline void vTaskDelay(unsigned ms) { std::this_thread::sleep_for(std::chrono::milliseconds(ms)); }
inline void vTaskDelete(void*) {}

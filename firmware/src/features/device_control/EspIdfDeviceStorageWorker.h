#pragma once

#include <atomic>

#include "DeviceStorageStartup.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"

// Boot-lifetime task adapter; it never invokes an actuator or control callback.
class EspIdfDeviceStorageWorker final {
 public:
  explicit EspIdfDeviceStorageWorker(DeviceStorageStartup& startup) : startup_(startup) {}
  bool Start();
  uint32_t MinimumStackFreeBytes() const { return stackFree_.load(); }
  uint32_t MaximumOperationMicros() const { return maximumMicros_.load(); }
  static constexpr uint32_t StackBytes = 32768;
 private:
  static void Entry(void* context);
  void Run();
  void Record(int64_t started);
  DeviceStorageStartup& startup_;
  SemaphoreHandle_t completed_ = nullptr;
  TaskHandle_t task_ = nullptr;
  bool attempted_ = false;
  std::atomic<uint32_t> stackFree_{0}, maximumMicros_{0};
};

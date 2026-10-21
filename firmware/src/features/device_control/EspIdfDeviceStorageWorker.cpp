#include "EspIdfDeviceStorageWorker.h"

#include <algorithm>
#include <limits>

#include "esp_timer.h"

bool EspIdfDeviceStorageWorker::Start() {
  if (attempted_) return false;
  attempted_ = true;
  completed_ = xSemaphoreCreateBinary();
  if (!completed_) return false;
  const bool started = xTaskCreate(Entry, "device_storage", StackBytes, this, 2, &task_) == pdPASS;
  // Startup precedes all admission and retains the initial disabled outputs.
  // A failed task creation never falls back to synchronous control storage.
  if (started) xSemaphoreTake(completed_, portMAX_DELAY);
  vSemaphoreDelete(completed_);
  completed_ = nullptr;
  return started;
}

void EspIdfDeviceStorageWorker::Entry(void* context) {
  static_cast<EspIdfDeviceStorageWorker*>(context)->Run();
}

void EspIdfDeviceStorageWorker::Record(int64_t started) {
  const int64_t elapsed = esp_timer_get_time() - started;
  const uint32_t micros = elapsed <= 0 ? 0 : static_cast<uint32_t>(std::min<int64_t>(elapsed,
      std::numeric_limits<uint32_t>::max()));
  if (micros > maximumMicros_.load()) maximumMicros_.store(micros);
  stackFree_.store(static_cast<uint32_t>(uxTaskGetStackHighWaterMark(nullptr)));
}

void EspIdfDeviceStorageWorker::Run() {
  const int64_t bootStarted = esp_timer_get_time();
  startup_.Boot();
  Record(bootStarted);
  xSemaphoreGive(completed_);
  // Service-required boot is not an invitation to establish/rebind on a timer.
  if (!startup_.Runtime()) vTaskDelete(nullptr);
  for (;;) {
    const int64_t started = esp_timer_get_time();
    startup_.RunOne();
    Record(started);
    vTaskDelay(std::max<TickType_t>(1, pdMS_TO_TICKS(2)));
  }
}

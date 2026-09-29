#pragma once

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

#include "NetworkSettings.h"
#include "../provisioning/DeviceIdentity.h"

class EspIdfBackendNetwork final {
 public:
  explicit EspIdfBackendNetwork(DeviceIdentity& identity);
  bool Start(bool nvsInitialized);

 private:
  static void TaskEntry(void* argument);
  void Run();

  DeviceIdentity& identity_;
  NetworkSettings settings_;
  TaskHandle_t task_ = nullptr;
};

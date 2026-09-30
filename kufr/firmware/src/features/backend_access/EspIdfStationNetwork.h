#pragma once

#include <atomic>

#include "EspIdfWifiStation.h"
#include "freertos/task.h"

// One boot-lifetime owner of the shared STA/netif lifecycle. Cloud failure
// never destroys the local network. Start is called once by the control owner.
class EspIdfStationNetwork final {
 public:
  bool Start(bool nvsInitialized);
  bool HasIp() const;
  const NetworkSettings& Settings() const;

 private:
  static void TaskEntry(void* context);
  void Run();

  NetworkSettings settings_;
  EspIdfWifiStation wifi_;
  std::atomic<bool> initialized_{false};
  TaskHandle_t task_ = nullptr;
  bool startRequested_ = false;
};

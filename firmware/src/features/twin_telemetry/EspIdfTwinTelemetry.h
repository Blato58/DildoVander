#pragma once

#include "TwinTelemetry.h"
#include "../backend_access/EspIdfStationNetwork.h"
#include "DeviceIdentity.h"

class EspIdfTwinTelemetry final {
 public:
  EspIdfTwinTelemetry(TwinTelemetryMailbox& mailbox, DeviceIdentity& identity,
      EspIdfStationNetwork& station, const std::string& bootId)
      : mailbox_(mailbox), identity_(identity), station_(station), bootId_(bootId) {}
  bool Start();
 private:
  static void Task(void* context);
  void Run();
  TwinTelemetryMailbox& mailbox_;
  DeviceIdentity& identity_;
  EspIdfStationNetwork& station_;
  std::string bootId_;
  TaskHandle_t task_ = nullptr;
};

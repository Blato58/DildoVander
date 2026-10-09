#pragma once

#include <stdint.h>
#include <string>

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "../provisioning/LocalPairingCredential.h"

class EspIdfStationNetwork;

// Boot-lifetime network-task owner. Discovery conveys public routing hints;
// it neither publishes credentials nor changes the control/safety owner.
class EspIdfLocalServiceAdvertisement final {
 public:
  EspIdfLocalServiceAdvertisement(EspIdfStationNetwork& station, Sha256Port& hasher);
  EspIdfLocalServiceAdvertisement(const EspIdfLocalServiceAdvertisement&) = delete;
  EspIdfLocalServiceAdvertisement& operator=(const EspIdfLocalServiceAdvertisement&) = delete;

  // Called only after the provisioned TLS server successfully starts.
  bool Start(const std::string& deviceId);
  // Same deterministic hostname is used in authenticated BLE enrollment.
  static std::string Hostname(const std::string& deviceId, Sha256Port& hasher);
  static bool SupportsLocalControl(const std::string& deviceId);

 private:
  static void TaskEntry(void* context);
  void Run();
  void Update(uint64_t nowMs, bool hasIp, uint32_t generation);
  void Unpublish();

  EspIdfStationNetwork& station_;
  Sha256Port& hasher_;
  std::string deviceId_;
  std::string hostname_;
  TaskHandle_t task_ = nullptr;
  bool initialized_ = false;
  uint32_t generation_ = 0;
  uint64_t retryAtMs_ = 0;
};

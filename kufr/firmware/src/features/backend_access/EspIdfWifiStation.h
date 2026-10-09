#pragma once

#include <stdint.h>
#include <atomic>
#include <string>

#include "esp_event.h"
#include "esp_netif.h"
#include "freertos/FreeRTOS.h"
#include "freertos/event_groups.h"

#include "NetworkSettings.h"

class EspIdfWifiStation final {
 public:
  EspIdfWifiStation() = default;
  ~EspIdfWifiStation();
  EspIdfWifiStation(const EspIdfWifiStation&) = delete;
  EspIdfWifiStation& operator=(const EspIdfWifiStation&) = delete;

  bool Start(const NetworkSettings& settings);
  bool Start();
  bool Select(const char* ssid, const char* password);
  void Disconnect();
  bool ConnectAndWait(uint32_t timeoutMs);
  bool HasIp() const;
  uint32_t IpGeneration() const;
  // Empty while offline or if the address generation changes during sampling.
  std::string CurrentIpv4() const;

 private:
  static void OnEvent(void* argument, esp_event_base_t eventBase,
                      int32_t eventId, void* eventData);

  static constexpr EventBits_t StartedBit = BIT0;
  static constexpr EventBits_t GotIpBit = BIT1;
  EventGroupHandle_t events_ = nullptr;
  esp_netif_t* netif_ = nullptr;
  esp_event_handler_instance_t wifiHandler_ = nullptr;
  esp_event_handler_instance_t ipHandler_ = nullptr;
  bool ownsEventLoop_ = false;
  bool wifiInitialized_ = false;
  bool wifiStarted_ = false;
  std::atomic<uint32_t> ipGeneration_{0};
};

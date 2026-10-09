#pragma once

#include <atomic>

#include "EspIdfWifiStation.h"
#include "freertos/task.h"
#include "freertos/semphr.h"
#include "../network_provisioning/EspIdfNetworkProfileStorage.h"

struct NetworkChangeGate {
  bool (*acquire)(void*) = nullptr;
  void (*release)(void*) = nullptr;
  void* context = nullptr;
};
struct StationNetworkStatus {
  char hotspotSsid[33]{};
  char fallbackSsid[33]{};
  NetworkProfile active = NetworkProfile::None;
  NetworkProfile lastSuccessful = NetworkProfile::None;
  const char* phase = "offline";
  const char* result = "idle";
};

// One boot-lifetime owner of the shared STA/netif lifecycle. Cloud failure
// never destroys the local network. Start is called once by the control owner.
class EspIdfStationNetwork final {
 public:
  bool Start(bool nvsInitialized);
  bool HasIp() const;
  uint32_t IpGeneration() const;
  const NetworkSettings& Settings() const;
  void SetChangeGate(NetworkChangeGate gate);
  bool Configure(NetworkProfile profile, const char* ssid, const char* password);
  bool Delete(NetworkProfile profile);
  bool Test(NetworkProfile profile);
  StationNetworkStatus Status();
  bool IsChangingNetwork() const { return changing_.load(); }

 private:
  static void TaskEntry(void* context);
  void Run();
  bool Queue(uint8_t operation, NetworkProfile profile, const WifiProfile* candidate);
  bool Connect(NetworkProfile profile, const WifiProfile& config);
  void Publish(const char* phase, const char* result);
  void ProcessRequest();

  NetworkSettings settings_;
  EspIdfWifiStation wifi_;
  std::atomic<bool> initialized_{false};
  TaskHandle_t task_ = nullptr;
  bool startRequested_ = false;
  EspIdfNetworkProfileStorage storage_;
  NetworkProfiles profiles_{storage_};
  SemaphoreHandle_t lock_ = nullptr;
  NetworkChangeGate gate_;
  uint8_t operation_ = 0;
  NetworkProfile requestedProfile_ = NetworkProfile::None;
  WifiProfile candidate_;
  NetworkProfile active_ = NetworkProfile::None;
  StationNetworkStatus status_;
  std::atomic<bool> changing_{false};
};

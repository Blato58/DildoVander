#include <cassert>
#include <cstring>
#include <vector>

#include "../src/features/backend_access/EspIdfStationNetwork.h"
#include "../src/features/backend_access/EspIdfNetworkSettings.h"

namespace {
TaskFunction_t task = nullptr;
void* argument = nullptr;
bool failTask = false;
bool failWifi = false;
bool ip = false;
int deleted = 0;
int starts = 0;
int connects = 0;
std::vector<uint32_t> delays;
struct EndTest {};
}

NetworkSettingsLoadResult LoadEspIdfNetworkSettings(NetworkSettings& settings,
                                                    bool nvsInitialized) {
  if (!nvsInitialized) {
    return NetworkSettingsLoadResult::Failed;
  }
  assert(settings.Configure("test-ssid", "test-password", "api.example.test"));
  return NetworkSettingsLoadResult::Loaded;
}

EspIdfWifiStation::~EspIdfWifiStation() = default;
bool EspIdfWifiStation::Start(const NetworkSettings& settings) {
  ++starts;
  assert(settings.IsConfigured());
  return !failWifi;
}
bool EspIdfWifiStation::HasIp() const { return ip; }
bool EspIdfWifiStation::ConnectAndWait(uint32_t timeoutMs) {
  assert(timeoutMs == 15000);
  ++connects;
  ip = connects == 3;
  return ip;
}
BaseType_t xTaskCreate(TaskFunction_t entry, const char* name, uint32_t stack,
                       void* context, unsigned priority, TaskHandle_t* handle) {
  assert(std::strcmp(name, "station_network") == 0);
  assert(stack == 4096 && priority == 4);
  if (failTask) {
    return 0;
  }
  task = entry;
  argument = context;
  *handle = reinterpret_cast<void*>(1);
  return pdPASS;
}
void vTaskDelete(TaskHandle_t handle) {
  assert(handle == nullptr);
  ++deleted;
}
void vTaskDelay(TickType_t ticks) {
  delays.push_back(ticks);
  if (ticks == 100) {
    assert(static_cast<EspIdfStationNetwork*>(argument)->HasIp());
    ip = false;  // Local association is lost without destroying its owner.
  }
  if (delays.size() == 4) {
    throw EndTest{};
  }
}
uint32_t esp_random() { return 0; }

int main() {
  EspIdfStationNetwork missing;
  assert(!missing.Start(false));
  assert(!missing.Settings().IsConfigured() && !missing.HasIp());
  assert(!missing.Start(true));
  EspIdfStationNetwork noTask;
  failTask = true;
  assert(!noTask.Start(true));
  assert(!noTask.Settings().IsConfigured() && !noTask.HasIp());
  failTask = false;
  EspIdfStationNetwork broken;
  failWifi = true;
  assert(broken.Start(true));
  task(argument);
  assert(deleted == 1 && !broken.HasIp());
  failWifi = false;
  EspIdfStationNetwork live;
  assert(live.Start(true));
  assert(!live.Start(true));
  assert(!live.HasIp());
  try {
    task(argument);
    assert(false);
  } catch (const EndTest&) {
  }
  assert(starts == 2 && connects == 4);
  assert((delays == std::vector<uint32_t>{800, 1600, 100, 800}));
  assert(!live.HasIp() && live.Settings().IsConfigured());
}

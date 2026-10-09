#include <cassert>
#include <cstring>
#include <vector>
#include "../src/features/backend_access/EspIdfStationNetwork.h"
#include "../src/features/backend_access/EspIdfNetworkSettings.h"

namespace {
TaskFunction_t task;
void* argument;
bool ip = false, gateAllowed = true, reserved = false, legacy = true, stored = false, saveFails = false;
int64_t clockMs = 0, stopAt = 0;
NetworkProfileRecord saved;
std::vector<std::string> attempts;
std::string selected;
struct EndTest {};
}
NetworkSettingsLoadResult LoadEspIdfNetworkSettings(NetworkSettings& settings, bool initialized) {
  if (!initialized) { return NetworkSettingsLoadResult::Failed; }
  if (!legacy) { return NetworkSettingsLoadResult::Missing; }
  assert(settings.Configure("owner-hotspot", "old-password", "api.example.test"));
  return NetworkSettingsLoadResult::Loaded;
}
bool EspIdfNetworkProfileStorage::Load(NetworkProfileRecord& record, bool& missing) {
  missing = !stored;
  if (stored) { record = saved; }
  return true;
}
bool EspIdfNetworkProfileStorage::Save(const NetworkProfileRecord& record) {
  if (saveFails) { return false; }
  stored = true;
  saved = record;
  return true;
}
EspIdfWifiStation::~EspIdfWifiStation() = default;
bool EspIdfWifiStation::Start() { return true; }
bool EspIdfWifiStation::Start(const NetworkSettings&) { return Start(); }
bool EspIdfWifiStation::Select(const char* ssid, const char*) {
  assert(reserved);
  selected = ssid;
  attempts.push_back(selected);
  ip = false;
  return true;
}
void EspIdfWifiStation::Disconnect() { ip = false; }
bool EspIdfWifiStation::HasIp() const { return ip; }
uint32_t EspIdfWifiStation::IpGeneration() const { return 7; }
bool EspIdfWifiStation::ConnectAndWait(uint32_t timeoutMs) {
  assert(timeoutMs == 15000 && reserved);
  ip = selected != "bad-password";
  return ip;
}
BaseType_t xTaskCreate(TaskFunction_t entry, const char* name, uint32_t stack, void* context,
                       unsigned priority, TaskHandle_t* handle) {
  assert(std::strcmp(name, "station_network") == 0 && stack == 6144 && priority == 4);
  task = entry; argument = context; *handle = reinterpret_cast<void*>(1); return pdPASS;
}
void vTaskDelete(TaskHandle_t) {}
void vTaskDelay(TickType_t ticks) {
  clockMs += ticks;
  if (clockMs >= stopAt) { throw EndTest{}; }
}
uint32_t esp_random() { return 0; }
int64_t esp_timer_get_time() { return clockMs * 1000; }
SemaphoreHandle_t xSemaphoreCreateMutex() { return reinterpret_cast<void*>(1); }
BaseType_t xSemaphoreTake(SemaphoreHandle_t, TickType_t) { return pdTRUE; }
BaseType_t xSemaphoreGive(SemaphoreHandle_t) { return pdTRUE; }
bool Acquire(void*) { if (!gateAllowed || reserved) { return false; } reserved = true; return true; }
void Release(void*) { assert(reserved); reserved = false; }
void RunFor(int64_t duration) {
  stopAt = clockMs + duration;
  try { task(argument); assert(false); } catch (const EndTest&) {}
}
int main() {
  EspIdfStationNetwork noNvs;
  assert(!noNvs.Start(false));
  EspIdfStationNetwork first;
  first.SetChangeGate({Acquire, Release, nullptr});
  assert(first.Start(true));
  assert(stored && std::strcmp(saved.hotspot.password, "old-password") == 0);
  assert(!first.Start(true));
  RunFor(100);
  assert(first.HasIp() && first.Status().active == NetworkProfile::Hotspot);
  assert(first.Configure(NetworkProfile::Hotspot, "bad-password", "invalid-pass"));
  assert(!first.Configure(NetworkProfile::Fallback, "duplicate", "password123"));
  RunFor(100);
  assert(first.Status().result == std::string("connection_failed"));
  assert(std::strcmp(saved.hotspot.ssid, "owner-hotspot") == 0);
  assert(first.HasIp() && attempts.back() == "owner-hotspot");
  saveFails = true;
  assert(first.Configure(NetworkProfile::Hotspot, "new-hotspot", "new-password"));
  RunFor(100);
  assert(first.Status().result == std::string("storage_failed"));
  assert(std::strcmp(saved.hotspot.ssid, "owner-hotspot") == 0);
  assert(attempts.back() == "owner-hotspot");
  saveFails = false;
  gateAllowed = false;
  assert(first.Delete(NetworkProfile::Hotspot));
  const size_t before = attempts.size();
  RunFor(100);
  assert(first.Status().result == std::string("busy"));
  assert(attempts.size() == before && saved.hotspot.Configured());
  gateAllowed = true;
  assert(first.Configure(NetworkProfile::Fallback, "event-wifi", "event-password"));
  RunFor(100);
  assert(saved.fallback.Configured());
  assert(first.Delete(NetworkProfile::Hotspot));
  RunFor(100);
  assert(!saved.hotspot.Configured() && saved.fallback.Configured());
  legacy = false;
  ip = false;
  EspIdfStationNetwork reboot;
  reboot.SetChangeGate({Acquire, Release, nullptr});
  assert(reboot.Start(true));
  RunFor(100);
  assert(reboot.HasIp() && reboot.Status().active == NetworkProfile::Fallback);
  assert(!reboot.Settings().IsConfigured()); // Wi-Fi remains independent from backend registration.
  ip = false;
  assert(NetworkProfiles::MakeCandidate("bad-password", "hotspot-password", saved.hotspot));
  attempts.clear();
  EspIdfStationNetwork fallback;
  fallback.SetChangeGate({Acquire, Release, nullptr});
  assert(fallback.Start(true));
  RunFor(1200);
  assert(attempts.size() == 2 && attempts[0] == "bad-password" && attempts[1] == "event-wifi");
  assert(fallback.HasIp() && fallback.Status().active == NetworkProfile::Fallback);
}

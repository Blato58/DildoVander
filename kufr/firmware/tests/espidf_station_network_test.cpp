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
uint32_t ipGeneration = 0;
bool changeDuringIpRead = false;
EspIdfStationNetwork* addressObserver = nullptr;
void (*delayHook)() = nullptr;
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
uint32_t EspIdfWifiStation::IpGeneration() const { return ipGeneration; }
std::string EspIdfWifiStation::CurrentIpv4() const {
  if (changeDuringIpRead) { ++ipGeneration; changeDuringIpRead = false; }
  return ip ? "192.168.5.37" : "";
}
bool EspIdfWifiStation::ConnectAndWait(uint32_t timeoutMs) {
  assert(timeoutMs == 15000 && reserved);
  ip = selected != "bad-password";
  if (ip) { ++ipGeneration; }
  if (addressObserver != nullptr) {
    // A newly acquired address is hidden until the profile operation settles.
    assert(addressObserver->CurrentIpv4().empty());
  }
  return ip;
}
BaseType_t xTaskCreate(TaskFunction_t entry, const char* name, uint32_t stack, void* context,
                       unsigned priority, TaskHandle_t* handle) {
  assert(std::strcmp(name, "station_network") == 0 && stack == 6144 && priority == 4);
  task = entry; argument = context; *handle = reinterpret_cast<void*>(1); return pdPASS;
}
void vTaskDelete(TaskHandle_t) {}
void vTaskDelay(TickType_t ticks) {
  if (delayHook != nullptr) { delayHook(); return; }
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
EspIdfStationNetwork* regressionStation = nullptr;
unsigned regressionStage = 0;
void VerifyExplicitFallbackProbeDelay() {
  switch (regressionStage++) {
    case 0:
      assert(attempts.size() == 1 && attempts.back() == "owner-hotspot");
      clockMs = 61000; // The automatic probe deadline has already expired.
      assert(regressionStation->Configure(NetworkProfile::Fallback, "verified-fallback", "fallback-password"));
      return;
    case 1:
      assert(attempts.size() == 2 && attempts.back() == "verified-fallback");
      assert(regressionStation->Status().active == NetworkProfile::Fallback);
      assert(regressionStation->Status().result == std::string("connected"));
      clockMs = 121001;
      assert(regressionStation->Test(NetworkProfile::Fallback));
      return;
    case 2:
      assert(attempts.size() == 3 && attempts.back() == "verified-fallback");
      assert(regressionStation->Status().active == NetworkProfile::Fallback);
      assert(regressionStation->Status().result == std::string("connected"));
      clockMs = 181002;
      assert(regressionStation->Configure(NetworkProfile::Hotspot, "bad-password", "wrong-password"));
      return;
    case 3:
      assert(attempts.size() == 5 && attempts[3] == "bad-password" && attempts[4] == "verified-fallback");
      assert(regressionStation->Status().active == NetworkProfile::Fallback);
      assert(regressionStation->Status().result == std::string("connection_failed"));
      clockMs = 241001; // 59999 ms since the successful rollback.
      return;
    case 4:
      assert(attempts.size() == 5 && regressionStation->Status().active == NetworkProfile::Fallback);
      assert(regressionStation->Status().result == std::string("connection_failed"));
      clockMs = 241003; // Automatic preference probing resumes after 60000 ms.
      return;
    default:
      assert(attempts.size() == 6 && attempts.back() == "owner-hotspot");
      assert(regressionStation->Status().active == NetworkProfile::Hotspot);
      throw EndTest{};
  }
}
int main() {
  EspIdfStationNetwork noNvs;
  assert(!noNvs.Start(false));
  assert(noNvs.CurrentIpv4().empty());
  EspIdfStationNetwork first;
  first.SetChangeGate({Acquire, Release, nullptr});
  assert(first.Start(true));
  assert(stored && std::strcmp(saved.hotspot.password, "old-password") == 0);
  assert(!first.Start(true));
  assert(first.CurrentIpv4().empty());
  addressObserver = &first;
  RunFor(100);
  addressObserver = nullptr;
  assert(first.HasIp() && first.Status().active == NetworkProfile::Hotspot);
  assert(first.CurrentIpv4() == "192.168.5.37");
  changeDuringIpRead = true;
  assert(first.CurrentIpv4().empty());
  assert(first.CurrentIpv4() == "192.168.5.37");
  ip = false;
  assert(first.CurrentIpv4().empty());
  ip = true;
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
  ip = false;
  clockMs = 0;
  assert(NetworkProfiles::MakeCandidate("owner-hotspot", "hotspot-password", saved.hotspot));
  attempts.clear();
  EspIdfStationNetwork explicitFallback;
  explicitFallback.SetChangeGate({Acquire, Release, nullptr});
  assert(explicitFallback.Start(true));
  regressionStation = &explicitFallback;
  delayHook = VerifyExplicitFallbackProbeDelay;
  try { task(argument); assert(false); } catch (const EndTest&) {}
  delayHook = nullptr;
  assert(regressionStage == 6);
}

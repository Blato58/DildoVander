#include <cassert>
#include <cstring>
#include <map>
#include <string>

#include "../src/features/local_control/EspIdfLocalServiceAdvertisement.h"
#include "../src/features/backend_access/EspIdfStationNetwork.h"
#include "mdns.h"

namespace {
TaskFunction_t entry = nullptr;
void* argument = nullptr;
bool failTask = false;
bool hasIp = false;
uint32_t generation = 0;
uint64_t nowMs = 0;
int ticks = 0;
int initCalls = 0;
int freeCalls = 0;
int serviceCalls = 0;
bool active = false;
bool failInit = false;
bool failHost = false;
bool failService = false;
std::string hostname;
std::map<std::string, std::string> txtValues;
struct EndTest {};

}

// The advertisement adapter alone is under test; no STA/task/network is live.
bool EspIdfNetworkProfileStorage::Save(const NetworkProfileRecord&) {
  assert(false);  // Discovery must never write network profiles.
  return false;
}
bool EspIdfStationNetwork::HasIp() const { return hasIp; }
uint32_t EspIdfStationNetwork::IpGeneration() const { return generation; }
EspIdfWifiStation::~EspIdfWifiStation() = default;
int64_t esp_timer_get_time() { return static_cast<int64_t>(nowMs * 1000); }
BaseType_t xTaskCreate(TaskFunction_t task, const char* name, uint32_t stack,
                      void* context, unsigned priority, TaskHandle_t* handle) {
  assert(std::strcmp(name, "local_discovery") == 0 && stack == 4096 && priority == 3);
  if (failTask) { return 0; }
  entry = task; argument = context; *handle = reinterpret_cast<void*>(1);
  return pdPASS;
}
void vTaskDelete(TaskHandle_t) { assert(false); }
void vTaskDelay(TickType_t delay) {
  assert(delay == 100);
  ++ticks;
  nowMs += 100;
  if (ticks == 3) { throw EndTest{}; }
}
esp_err_t mdns_init() {
  ++initCalls;
  assert(!active);
  active = !failInit;
  return failInit ? -1 : ESP_OK;
}
void mdns_free() { assert(active); active = false; ++freeCalls; }
esp_err_t mdns_hostname_set(const char* host) {
  assert(active);
  hostname = host;
  assert(hostname.size() == 37 && hostname.substr(0, 5) == "kufr-");
  return failHost ? -1 : ESP_OK;
}
esp_err_t mdns_service_add(const char* instance, const char* service, const char* protocol,
                         uint16_t port, mdns_txt_item_t* txt, size_t count) {
  assert(active && hostname == instance && std::strcmp(service, "_kufr") == 0 &&
         std::strcmp(protocol, "_tcp") == 0 && port == 443 && count == 3);
  txtValues.clear();
  for (size_t index = 0; index < count; ++index) { txtValues[txt[index].key] = txt[index].value; }
  assert(txtValues.size() == 3 && txtValues["protocol"] == "1");
  assert(txtValues["capabilities"] == "manualControl,inventoryReload,conversationText,voiceRealtime");
  ++serviceCalls;
  return failService ? -1 : ESP_OK;
}

class TestHasher final : public Sha256Port {
 public:
  bool fail = false;
  bool Hash(const uint8_t* bytes, size_t count, std::array<uint8_t, 32>& digest) override {
    assert(count <= 64 && bytes != nullptr);
    digest.fill(0xab);
    return !fail;
  }
};

void RunSample() {
  ticks = 0;
  try { entry(argument); assert(false); } catch (const EndTest&) {}
}

int main() {
  EspIdfStationNetwork station;
  TestHasher hash;
  EspIdfLocalServiceAdvertisement advertisement(station, hash);
  for (const char* legacyId : {"kufr_1", "Kufr-001", "1kufr"}) {
    const auto host = EspIdfLocalServiceAdvertisement::Hostname(legacyId, hash);
    assert(host.size() == 37 && host.substr(0, 5) == "kufr-");
    assert(!EspIdfLocalServiceAdvertisement::SupportsLocalControl(legacyId));
  }
  assert(EspIdfLocalServiceAdvertisement::Hostname("", hash).empty());
  assert(EspIdfLocalServiceAdvertisement::Hostname(std::string(65, 'a'), hash).empty());
  assert(EspIdfLocalServiceAdvertisement::SupportsLocalControl("kufr-001"));
  assert(!advertisement.Start("") && !advertisement.Start("bad/id"));
  assert(!advertisement.Start(std::string(65, 'a')));
  assert(!advertisement.Start("Kufr-001") && !advertisement.Start("kufr_001") &&
         !advertisement.Start("1kufr"));
  hash.fail = true;
  assert(!advertisement.Start("kufr-001"));
  hash.fail = false;
  failTask = true;
  assert(!advertisement.Start("kufr-001"));
  failTask = false;
  assert(advertisement.Start("kufr-001") && !advertisement.Start("kufr-001"));
  RunSample();
  assert(initCalls == 0 && serviceCalls == 0);  // No-IP is not advertised.
  hasIp = true; generation = 1;
  RunSample();
  assert(initCalls == 1 && serviceCalls == 1 && active);
  assert(txtValues["deviceId"] == "kufr-001");
  RunSample();
  assert(initCalls == 1);  // Stable IP does not recreate the daemon.
  generation = 3;  // An IP loss/replacement occurred between samples.
  RunSample();
  assert(freeCalls == 1 && initCalls == 2 && serviceCalls == 2);
  hasIp = false;
  RunSample();
  assert(freeCalls == 2 && !active);
  hasIp = true; generation = 4; failInit = true;
  RunSample();
  const int attempts = initCalls;
  RunSample();
  assert(initCalls == attempts);  // Failed initialization backs off 5 s.
  failInit = false; nowMs += 5000; failHost = true;
  RunSample();
  assert(!active && freeCalls == 3);
  failHost = false; nowMs += 5000; failService = true;
  RunSample();
  assert(!active && freeCalls == 4);
  failService = false; nowMs += 5000;
  RunSample();
  assert(active);
  hasIp = false;
  RunSample();
  assert(!active && freeCalls == 5);
}

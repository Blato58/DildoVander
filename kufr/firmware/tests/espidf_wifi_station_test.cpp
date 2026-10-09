#include <cassert>
#include <cstring>
#include <cstdio>
#include "../src/features/backend_access/EspIdfWifiStation.h"
#include "esp_wifi.h"

const char* const WIFI_EVENT = "wifi";
const char* const IP_EVENT = "ip";
namespace {
esp_event_handler_t wifiCallback = nullptr;
esp_event_handler_t ipCallback = nullptr;
void* context = nullptr;
EventBits_t bits = 0;
esp_netif_t netif;
int unregisters = 0;
uint32_t currentAddress = 0x2505a8c0; // 192.168.5.37 in network byte order on this host.
int ipReads = 0;
int changeDuringRead = 0;
bool ipReadFails = false;
bool ipFormatFails = false;
}
esp_err_t esp_event_loop_create_default() { return ESP_OK; }
esp_err_t esp_event_loop_delete_default() { return ESP_OK; }
esp_err_t esp_netif_init() { return ESP_OK; }
esp_netif_t* esp_netif_create_default_wifi_sta() { return &netif; }
void esp_netif_destroy_default_wifi(esp_netif_t* value) { assert(value == &netif); }
esp_err_t esp_netif_get_ip_info(esp_netif_t* value, esp_netif_ip_info_t* info) {
  assert(value == &netif);
  ++ipReads;
  if (ipReadFails) { return ESP_ERR_INVALID_STATE; }
  info->ip.addr = currentAddress;
  if (changeDuringRead != 0) {
    ipCallback(context, IP_EVENT, IP_EVENT_STA_LOST_IP, nullptr);
    if (changeDuringRead == 2) {
      currentAddress = 0x2605a8c0;
      ipCallback(context, IP_EVENT, IP_EVENT_STA_GOT_IP, nullptr);
    }
    changeDuringRead = 0;
  }
  return ESP_OK;
}
char* esp_ip4addr_ntoa(const esp_ip4_addr_t* ip, char* output, int length) {
  if (ipFormatFails) { return nullptr; }
  const int written = std::snprintf(output, static_cast<size_t>(length), "%u.%u.%u.%u",
      static_cast<unsigned>(ip->addr & 255), static_cast<unsigned>((ip->addr >> 8) & 255),
      static_cast<unsigned>((ip->addr >> 16) & 255), static_cast<unsigned>(ip->addr >> 24));
  return written > 0 && written < length ? output : nullptr;
}
esp_err_t esp_event_handler_instance_register(esp_event_base_t base, int32_t event,
    esp_event_handler_t callback, void* argument, esp_event_handler_instance_t* handle) {
  assert(event == ESP_EVENT_ANY_ID);
  context = argument;
  (base == WIFI_EVENT ? wifiCallback : ipCallback) = callback;
  *handle = reinterpret_cast<void*>(1);
  return ESP_OK;
}
esp_err_t esp_event_handler_instance_unregister(esp_event_base_t, int32_t event,
    esp_event_handler_instance_t) {
  assert(event == ESP_EVENT_ANY_ID); ++unregisters; return ESP_OK;
}
esp_err_t esp_wifi_init(wifi_init_config_t*) { return ESP_OK; }
esp_err_t esp_wifi_deinit() { return ESP_OK; }
esp_err_t esp_wifi_set_storage(int value) { assert(value == WIFI_STORAGE_RAM); return ESP_OK; }
esp_err_t esp_wifi_set_mode(int value) { assert(value == WIFI_MODE_STA); return ESP_OK; }
esp_err_t esp_wifi_set_config(int iface, wifi_config_t* config) {
  assert(iface == WIFI_IF_STA && config->sta.threshold.authmode == WIFI_AUTH_WPA2_PSK);
  assert(std::memcmp(config->sta.ssid, "test-ssid", 9) == 0);
  return ESP_OK;
}
esp_err_t esp_wifi_start() { wifiCallback(context, WIFI_EVENT, WIFI_EVENT_STA_START, nullptr); return ESP_OK; }
esp_err_t esp_wifi_stop() { return ESP_OK; }
esp_err_t esp_wifi_connect() { ipCallback(context, IP_EVENT, IP_EVENT_STA_GOT_IP, nullptr); return ESP_OK; }
esp_err_t esp_wifi_disconnect() { return ESP_OK; }
EventGroupHandle_t xEventGroupCreate() { return &bits; }
void vEventGroupDelete(EventGroupHandle_t) {}
EventBits_t xEventGroupSetBits(EventGroupHandle_t, EventBits_t value) { return bits |= value; }
EventBits_t xEventGroupClearBits(EventGroupHandle_t, EventBits_t value) { return bits &= ~value; }
EventBits_t xEventGroupGetBits(EventGroupHandle_t) { return bits; }
EventBits_t xEventGroupWaitBits(EventGroupHandle_t, EventBits_t, int, int, TickType_t) { return bits; }

void TestCurrentIpv4Sampling() {
  EspIdfWifiStation station;
  assert(station.CurrentIpv4().empty() && ipReads == 0);
  NetworkSettings settings;
  assert(settings.Configure("test-ssid", "test-password", "api.example.test"));
  assert(station.Start(settings));
  assert(station.CurrentIpv4().empty() && ipReads == 0);
  assert(station.ConnectAndWait(15000));
  assert(station.CurrentIpv4() == "192.168.5.37");
  ipReadFails = true;
  assert(station.CurrentIpv4().empty());
  ipReadFails = false;
  ipFormatFails = true;
  assert(station.CurrentIpv4().empty());
  ipFormatFails = false;
  currentAddress = 0;
  assert(station.CurrentIpv4().empty());
  currentAddress = 0x2505a8c0;
  changeDuringRead = 2;
  const uint32_t generation = station.IpGeneration();
  assert(station.CurrentIpv4().empty());
  assert(station.HasIp() && station.IpGeneration() == generation + 2);
  assert(station.CurrentIpv4() == "192.168.5.38");
  changeDuringRead = 1;
  assert(station.CurrentIpv4().empty() && !station.HasIp());
  const int reads = ipReads;
  assert(station.CurrentIpv4().empty() && ipReads == reads);
  ipCallback(context, IP_EVENT, IP_EVENT_STA_GOT_IP, nullptr);
  assert(station.CurrentIpv4() == "192.168.5.38");
  station.Disconnect();
  assert(station.CurrentIpv4().empty());
}

int main() {
  {
    EspIdfWifiStation station;
    NetworkSettings settings;
    assert(settings.Configure("test-ssid", "test-password", "api.example.test"));
    assert(station.Start(settings) && !station.Start(settings));
    assert(!station.HasIp() && station.IpGeneration() == 0);
    assert(station.ConnectAndWait(15000));
    assert(station.HasIp() && station.IpGeneration() == 1);
    ipCallback(context, IP_EVENT, IP_EVENT_STA_LOST_IP, nullptr);
    assert(!station.HasIp() && station.IpGeneration() == 2);
    ipCallback(context, IP_EVENT, IP_EVENT_STA_GOT_IP, nullptr);
    assert(station.HasIp() && station.IpGeneration() == 3);
    wifiCallback(context, WIFI_EVENT, WIFI_EVENT_STA_DISCONNECTED, nullptr);
    assert(!station.HasIp() && station.IpGeneration() == 4);
    ipCallback(context, IP_EVENT, 999, nullptr);
    assert(station.IpGeneration() == 4);
  }
  assert(unregisters == 2);
  TestCurrentIpv4Sampling();
  assert(unregisters == 4);
}

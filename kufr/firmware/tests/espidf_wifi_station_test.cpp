#include <cassert>
#include <cstring>
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
}
esp_err_t esp_event_loop_create_default() { return ESP_OK; }
esp_err_t esp_event_loop_delete_default() { return ESP_OK; }
esp_err_t esp_netif_init() { return ESP_OK; }
esp_netif_t* esp_netif_create_default_wifi_sta() { return &netif; }
void esp_netif_destroy_default_wifi(esp_netif_t* value) { assert(value == &netif); }
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
}

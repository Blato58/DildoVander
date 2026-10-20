#include "EspIdfWifiStation.h"

#include <cstring>

#include "esp_wifi.h"

namespace {

class WifiConfigWiper final {
 public:
  explicit WifiConfigWiper(wifi_config_t& config) : config_(config) {}
  ~WifiConfigWiper() {
    volatile uint8_t* bytes = reinterpret_cast<uint8_t*>(&config_);
    for (size_t index = 0; index < sizeof(config_); ++index) {
      bytes[index] = 0;
    }
  }

 private:
  wifi_config_t& config_;
};

}  // namespace

EspIdfWifiStation::~EspIdfWifiStation() {
  if (wifiStarted_) {
    esp_wifi_stop();
  }
  if (wifiHandler_ != nullptr) {
    esp_event_handler_instance_unregister(WIFI_EVENT, ESP_EVENT_ANY_ID,
                                          wifiHandler_);
  }
  if (ipHandler_ != nullptr) {
    esp_event_handler_instance_unregister(IP_EVENT, ESP_EVENT_ANY_ID,
                                          ipHandler_);
  }
  if (wifiInitialized_) {
    esp_wifi_deinit();
  }
  if (netif_ != nullptr) {
    esp_netif_destroy_default_wifi(netif_);
  }
  if (ownsEventLoop_) {
    esp_event_loop_delete_default();
  }
  if (events_ != nullptr) {
    vEventGroupDelete(events_);
  }
}

bool EspIdfWifiStation::Start(const NetworkSettings& settings) {
  return settings.IsConfigured() && Start() && Select(settings.Ssid(), settings.Password());
}

bool EspIdfWifiStation::Start() {
  if (events_ != nullptr) {
    return false;
  }
  events_ = xEventGroupCreate();
  if (events_ == nullptr || esp_netif_init() != ESP_OK) {
    return false;
  }

  const esp_err_t loopResult = esp_event_loop_create_default();
  if (loopResult == ESP_OK) {
    ownsEventLoop_ = true;
  } else if (loopResult != ESP_ERR_INVALID_STATE) {
    return false;
  }
  netif_ = esp_netif_create_default_wifi_sta();
  if (netif_ == nullptr) {
    return false;
  }

  wifi_init_config_t wifiInit = WIFI_INIT_CONFIG_DEFAULT();
  if (esp_wifi_init(&wifiInit) != ESP_OK) {
    return false;
  }
  wifiInitialized_ = true;
  if (esp_event_handler_instance_register(
          WIFI_EVENT, ESP_EVENT_ANY_ID, OnEvent, this,
          &wifiHandler_) != ESP_OK ||
      esp_event_handler_instance_register(
          IP_EVENT, ESP_EVENT_ANY_ID, OnEvent, this,
          &ipHandler_) != ESP_OK ||
      esp_wifi_set_storage(WIFI_STORAGE_RAM) != ESP_OK ||
      esp_wifi_set_mode(WIFI_MODE_STA) != ESP_OK) {
    return false;
  }

  if (esp_wifi_start() != ESP_OK) {
    return false;
  }
  wifiStarted_ = true;
  return true;
}

void EspIdfWifiStation::Disconnect() {
  if (wifiStarted_) {
    const bool hadIp = HasIp();
    esp_wifi_disconnect();
    xEventGroupClearBits(events_, GotIpBit);
    if (hadIp) { ipGeneration_.fetch_add(1); }
  }
}

bool EspIdfWifiStation::Select(const char* ssid, const char* password) {
  if (!wifiStarted_ || ssid == nullptr || password == nullptr ||
      std::strlen(ssid) == 0 || std::strlen(ssid) > 32 ||
      std::strlen(password) < 8 || std::strlen(password) > 63) { return false; }
  if ((xEventGroupWaitBits(events_, StartedBit, pdFALSE, pdTRUE,
                          pdMS_TO_TICKS(5000)) & StartedBit) == 0) { return false; }
  Disconnect();
  wifi_config_t wifiConfig = {};
  WifiConfigWiper wipe(wifiConfig);
  const size_t ssidLength = std::strlen(ssid);
  const size_t passwordLength = std::strlen(password);
  std::memcpy(wifiConfig.sta.ssid, ssid, ssidLength);
  std::memcpy(wifiConfig.sta.password, password, passwordLength);
  wifiConfig.sta.threshold.authmode = WIFI_AUTH_WPA2_PSK;
  if (esp_wifi_set_config(WIFI_IF_STA, &wifiConfig) != ESP_OK) {
    return false;
  }
  return true;
}

bool EspIdfWifiStation::ConnectAndWait(uint32_t timeoutMs) {
  if (!wifiStarted_ ||
      (xEventGroupWaitBits(events_, StartedBit, pdFALSE, pdTRUE,
                           pdMS_TO_TICKS(5000)) & StartedBit) == 0) {
    return false;
  }
  if (HasIp()) {
    return true;
  }
  if (esp_wifi_connect() != ESP_OK && !HasIp()) {
    return false;
  }
  return (xEventGroupWaitBits(events_, GotIpBit, pdFALSE, pdTRUE,
                              pdMS_TO_TICKS(timeoutMs)) & GotIpBit) != 0;
}

bool EspIdfWifiStation::HasIp() const {
  return events_ != nullptr &&
         (xEventGroupGetBits(events_) & GotIpBit) != 0;
}

uint32_t EspIdfWifiStation::IpGeneration() const { return ipGeneration_.load(); }

std::string EspIdfWifiStation::CurrentIpv4() const {
  const uint32_t generation = IpGeneration();
  if (netif_ == nullptr || !HasIp()) { return {}; }
  esp_netif_ip_info_t info{};
  if (esp_netif_get_ip_info(netif_, &info) != ESP_OK || info.ip.addr == 0) {
    return {};
  }
  char address[16]{};
  if (esp_ip4addr_ntoa(&info.ip, address, static_cast<int>(sizeof(address))) == nullptr ||
      !HasIp() || IpGeneration() != generation) {
    return {};
  }
  return address;
}

void EspIdfWifiStation::OnEvent(void* argument,
                                esp_event_base_t eventBase, int32_t eventId,
                                void*) {
  EspIdfWifiStation* station = static_cast<EspIdfWifiStation*>(argument);
  if (eventBase == WIFI_EVENT && eventId == WIFI_EVENT_STA_START) {
    xEventGroupSetBits(station->events_, StartedBit);
  } else if (eventBase == WIFI_EVENT &&
             eventId == WIFI_EVENT_STA_DISCONNECTED) {
    xEventGroupClearBits(station->events_, GotIpBit);
    station->ipGeneration_.fetch_add(1);
  } else if (eventBase == IP_EVENT && eventId == IP_EVENT_STA_GOT_IP) {
    station->ipGeneration_.fetch_add(1);
    xEventGroupSetBits(station->events_, GotIpBit);
  } else if (eventBase == IP_EVENT && eventId == IP_EVENT_STA_LOST_IP) {
    xEventGroupClearBits(station->events_, GotIpBit);
    station->ipGeneration_.fetch_add(1);
  }
}

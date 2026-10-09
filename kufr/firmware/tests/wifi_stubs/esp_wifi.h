#pragma once
#include "esp_event.h"
struct wifi_init_config_t {};
#define WIFI_INIT_CONFIG_DEFAULT() wifi_init_config_t{}
struct wifi_config_t {
  struct { unsigned char ssid[32]; unsigned char password[64];
    struct { int authmode; } threshold;
  } sta;
};
constexpr int WIFI_STORAGE_RAM = 1;
constexpr int WIFI_MODE_STA = 2;
constexpr int WIFI_AUTH_WPA2_PSK = 3;
constexpr int WIFI_IF_STA = 4;
esp_err_t esp_wifi_init(wifi_init_config_t*);
esp_err_t esp_wifi_deinit();
esp_err_t esp_wifi_set_storage(int);
esp_err_t esp_wifi_set_mode(int);
esp_err_t esp_wifi_set_config(int, wifi_config_t*);
esp_err_t esp_wifi_start();
esp_err_t esp_wifi_stop();
esp_err_t esp_wifi_connect();
esp_err_t esp_wifi_disconnect();

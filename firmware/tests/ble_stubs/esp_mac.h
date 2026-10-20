#pragma once
#include "protocomm.h"
constexpr int ESP_MAC_WIFI_STA=1;
esp_err_t esp_read_mac(uint8_t*,int);

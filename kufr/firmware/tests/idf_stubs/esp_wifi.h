#pragma once

struct wifi_ap_record_t {};
constexpr int ESP_OK = 0;

int esp_wifi_sta_get_ap_info(wifi_ap_record_t* record);

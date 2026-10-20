#pragma once
#include "protocomm.h"
constexpr int MAX_BLE_DEVNAME_LEN=29;
struct protocomm_ble_name_uuid_t { const char* name; uint16_t uuid; };
struct protocomm_ble_config_t {
 char device_name[30]; uint8_t service_uuid[16]; ssize_t nu_lookup_count;
 protocomm_ble_name_uuid_t* nu_lookup; bool keep_ble_on;
};
esp_err_t protocomm_ble_start(protocomm_t*,const protocomm_ble_config_t*);
esp_err_t protocomm_ble_stop(protocomm_t*);

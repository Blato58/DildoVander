#pragma once
#include <stdint.h>
using esp_err_t = int;
constexpr esp_err_t ESP_OK = 0;
constexpr esp_err_t ESP_ERR_INVALID_STATE = 1;
using esp_event_base_t = const char*;
using esp_event_handler_instance_t = void*;
using esp_event_handler_t = void (*)(void*, esp_event_base_t, int32_t, void*);
extern const char* const WIFI_EVENT;
extern const char* const IP_EVENT;
constexpr int32_t ESP_EVENT_ANY_ID = -1;
constexpr int32_t WIFI_EVENT_STA_START = 1;
constexpr int32_t WIFI_EVENT_STA_DISCONNECTED = 2;
constexpr int32_t IP_EVENT_STA_GOT_IP = 3;
constexpr int32_t IP_EVENT_STA_LOST_IP = 4;
esp_err_t esp_event_loop_create_default();
esp_err_t esp_event_loop_delete_default();
esp_err_t esp_event_handler_instance_register(esp_event_base_t, int32_t, esp_event_handler_t,
                                             void*, esp_event_handler_instance_t*);
esp_err_t esp_event_handler_instance_unregister(esp_event_base_t, int32_t, esp_event_handler_instance_t);

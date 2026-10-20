#pragma once
#include <stddef.h>
#include <stdint.h>
using esp_err_t = int;
constexpr esp_err_t ESP_OK = 0;
struct mdns_txt_item_t { const char* key; const char* value; };
esp_err_t mdns_init();
void mdns_free();
esp_err_t mdns_hostname_set(const char*);
esp_err_t mdns_service_add(const char*, const char*, const char*, uint16_t,
                          mdns_txt_item_t*, size_t);

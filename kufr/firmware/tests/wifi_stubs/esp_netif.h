#pragma once
#include "esp_event.h"
struct esp_netif_t {};
struct esp_ip4_addr_t { uint32_t addr; };
struct esp_netif_ip_info_t { esp_ip4_addr_t ip; esp_ip4_addr_t netmask; esp_ip4_addr_t gw; };
esp_err_t esp_netif_get_ip_info(esp_netif_t*, esp_netif_ip_info_t*);
char* esp_ip4addr_ntoa(const esp_ip4_addr_t*, char*, int);
esp_err_t esp_netif_init();
esp_netif_t* esp_netif_create_default_wifi_sta();
void esp_netif_destroy_default_wifi(esp_netif_t*);

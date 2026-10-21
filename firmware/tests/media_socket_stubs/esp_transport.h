#pragma once
#include <cstddef>
extern "C" {
typedef int esp_err_t;
struct esp_transport_item_t;
typedef esp_transport_item_t* esp_transport_handle_t;
int esp_transport_connect(esp_transport_handle_t, const char*, int, int);
int esp_transport_read(esp_transport_handle_t, char*, int, int);
int esp_transport_write(esp_transport_handle_t, const char*, int, int);
int esp_transport_close(esp_transport_handle_t);
int esp_transport_get_socket(esp_transport_handle_t);
esp_err_t esp_transport_destroy(esp_transport_handle_t);
}

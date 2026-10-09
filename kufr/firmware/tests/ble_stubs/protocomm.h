#pragma once
#include <stdint.h>
#include <stddef.h>
#include "esp_event.h"
using ssize_t = ptrdiff_t;
constexpr esp_err_t ESP_ERR_INVALID_ARG = 2, ESP_ERR_NO_MEM = 3, ESP_ERR_INVALID_SIZE = 4;
struct protocomm_t {};
struct protocomm_security_t {};
struct protocomm_security1_params_t { const uint8_t* data; uint16_t len; };
using protocomm_req_handler_t = esp_err_t (*)(uint32_t,const uint8_t*,ssize_t,uint8_t**,ssize_t*,void*);
protocomm_t* protocomm_new();
void protocomm_delete(protocomm_t*);
esp_err_t protocomm_set_version(protocomm_t*, const char*, const char*);
esp_err_t protocomm_set_security(protocomm_t*,const char*,const protocomm_security_t*,const void*);
esp_err_t protocomm_add_endpoint(protocomm_t*,const char*,protocomm_req_handler_t,void*);

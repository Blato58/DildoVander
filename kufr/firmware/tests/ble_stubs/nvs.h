#pragma once
#include "protocomm.h"
using nvs_handle_t=unsigned;
constexpr int NVS_READONLY=0;
constexpr esp_err_t ESP_ERR_NVS_NOT_FOUND=5;
esp_err_t nvs_open(const char*,int,nvs_handle_t*);
esp_err_t nvs_get_str(nvs_handle_t,const char*,char*,size_t*);
void nvs_close(nvs_handle_t);

#pragma once
#include "freertos/FreeRTOS.h"
using EventGroupHandle_t = void*;
using EventBits_t = uint32_t;
constexpr int pdFALSE = 0;
constexpr int pdTRUE = 1;
EventGroupHandle_t xEventGroupCreate();
void vEventGroupDelete(EventGroupHandle_t);
EventBits_t xEventGroupSetBits(EventGroupHandle_t, EventBits_t);
EventBits_t xEventGroupClearBits(EventGroupHandle_t, EventBits_t);
EventBits_t xEventGroupGetBits(EventGroupHandle_t);
EventBits_t xEventGroupWaitBits(EventGroupHandle_t, EventBits_t, int, int, TickType_t);

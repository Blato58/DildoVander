#pragma once

#include "FreeRTOS.h"

struct FakeStorageTask;
using TaskHandle_t = FakeStorageTask*;
using TaskFunction_t = void (*)(void*);

BaseType_t xTaskCreate(TaskFunction_t entry, const char* name, uint32_t stackBytes,
    void* context, UBaseType_t priority, TaskHandle_t* handle);
void vTaskDelete(TaskHandle_t handle);
void vTaskDelay(TickType_t ticks);
UBaseType_t uxTaskGetStackHighWaterMark(TaskHandle_t handle);

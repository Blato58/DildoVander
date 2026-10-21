#pragma once

#include "FreeRTOS.h"

struct FakeStorageSemaphore;
using SemaphoreHandle_t = FakeStorageSemaphore*;

SemaphoreHandle_t xSemaphoreCreateBinary();
BaseType_t xSemaphoreTake(SemaphoreHandle_t handle, TickType_t timeout);
BaseType_t xSemaphoreGive(SemaphoreHandle_t handle);
void vSemaphoreDelete(SemaphoreHandle_t handle);

#include <inttypes.h>
#include <stdbool.h>
#include <stdint.h>

#include "esp_chip_info.h"
#include "esp_flash.h"
#include "esp_heap_caps.h"
#include "esp_log.h"
#include "esp_psram.h"
#include "esp_system.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

static const char *TAG = "board_memory";
static const size_t EXPECTED_PSRAM_BYTES = 8U * 1024U * 1024U;
static const uint32_t EXPECTED_FLASH_BYTES = 16U * 1024U * 1024U;
static const size_t TEST_BYTES = 7U * 1024U * 1024U;

static void yield_periodically(size_t word_index)
{
    // Let the idle tasks feed their watchdogs during each large memory sweep.
    if ((word_index & 0xffffU) == 0)
    {
        vTaskDelay(1);
    }
}

static bool test_pattern(volatile uint32_t *memory, size_t words, uint32_t pattern)
{
    // Address-dependent values also expose aliasing between distinct addresses.
    for (size_t index = 0; index < words; ++index)
    {
        memory[index] = (uint32_t)index ^ pattern;
        yield_periodically(index);
    }

    for (size_t index = 0; index < words; ++index)
    {
        uint32_t expected = (uint32_t)index ^ pattern;
        uint32_t actual = memory[index];
        if (actual != expected)
        {
            ESP_LOGE(TAG, "Mismatch word=%zu expected=%08" PRIx32 " actual=%08" PRIx32,
                     index, expected, actual);
            return false;
        }
        yield_periodically(index);
    }

    ESP_LOGI(TAG, "PATTERN PASS xor=%08" PRIx32 " bytes=%zu", pattern, words * sizeof(uint32_t));
    return true;
}

static bool run_memory_diagnostic(void)
{
    esp_chip_info_t chip;
    esp_chip_info(&chip);
    ESP_LOGI(TAG, "BEGIN diagnostic=v1 idf=%s chip=%d revision=%u cores=%u reset=%d",
             esp_get_idf_version(), chip.model, chip.revision, chip.cores, esp_reset_reason());

    uint32_t flash_bytes = 0;
    esp_err_t flash_result = esp_flash_get_size(NULL, &flash_bytes);
    size_t psram_bytes = esp_psram_get_size();
    ESP_LOGI(TAG, "MEMORY flash_bytes=%" PRIu32 " psram_bytes=%zu initialized=%d speed_mhz=%d",
             flash_bytes, psram_bytes, esp_psram_is_initialized(), CONFIG_SPIRAM_SPEED);

    if (chip.model != CHIP_ESP32S3 || chip.cores != 2 ||
        flash_result != ESP_OK || flash_bytes != EXPECTED_FLASH_BYTES ||
        !esp_psram_is_initialized() || psram_bytes != EXPECTED_PSRAM_BYTES)
    {
        ESP_LOGE(TAG, "Unexpected chip/memory identity flash_error=%d", flash_result);
        return false;
    }

    uint32_t capabilities = MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT;
    ESP_LOGI(TAG, "HEAP external_free=%zu external_largest=%zu internal_free=%zu",
             heap_caps_get_free_size(capabilities),
             heap_caps_get_largest_free_block(capabilities),
             heap_caps_get_free_size(MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT));

    uint32_t *memory = heap_caps_malloc(TEST_BYTES, capabilities);
    if (memory == NULL)
    {
        ESP_LOGE(TAG, "Cannot allocate %zu bytes of PSRAM", TEST_BYTES);
        return false;
    }

    const uint32_t patterns[] = {0x00000000U, 0xffffffffU, 0xaaaaaaaaU, 0x55555555U};
    bool passed = true;
    for (size_t index = 0; index < sizeof(patterns) / sizeof(patterns[0]); ++index)
    {
        if (!test_pattern(memory, TEST_BYTES / sizeof(uint32_t), patterns[index]))
        {
            passed = false;
            break;
        }
    }

    heap_caps_free(memory);
    bool heap_valid = heap_caps_check_integrity_all(true);
    ESP_LOGI(TAG, "HEAP integrity=%s", heap_valid ? "PASS" : "FAIL");
    return passed && heap_valid;
}

void app_main(void)
{
    bool passed = run_memory_diagnostic();
    ESP_LOGI(TAG, "RESULT %s", passed ? "PASS" : "FAIL");

    uint32_t heartbeat = 0;
    for (;;)
    {
        vTaskDelay(pdMS_TO_TICKS(5000));
        ESP_LOGI(TAG, "ALIVE count=%" PRIu32 " result=%s internal_free=%zu external_free=%zu",
                 ++heartbeat, passed ? "PASS" : "FAIL",
                 heap_caps_get_free_size(MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT),
                 heap_caps_get_free_size(MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT));
    }
}

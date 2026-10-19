#include <stdbool.h>
#include <stdint.h>

#include "driver/i2c_master.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

static const char *TAG = "pca_logic";
static const uint16_t PCA_ADDRESS = 0x40;
static const int TRANSFER_TIMEOUT_MS = 100;
static const unsigned SAMPLE_COUNT = 5;

static esp_err_t read_register(i2c_master_dev_handle_t device, uint8_t address, uint8_t *value)
{
    // Only select the register pointer. Never append a register value/PWM write.
    return i2c_master_transmit_receive(
        device, &address, sizeof(address), value, sizeof(*value), TRANSFER_TIMEOUT_MS);
}

static bool read_mode_samples(i2c_master_dev_handle_t device)
{
    uint8_t initial_mode1 = 0;
    uint8_t initial_mode2 = 0;
    for (unsigned sample = 1; sample <= SAMPLE_COUNT; ++sample)
    {
        uint8_t mode1 = 0;
        uint8_t mode2 = 0;
        esp_err_t result = read_register(device, 0x00, &mode1);
        if (result == ESP_OK)
        {
            result = read_register(device, 0x01, &mode2);
        }
        if (result != ESP_OK)
        {
            ESP_LOGE(TAG, "READ FAIL sample=%u error=%s", sample, esp_err_to_name(result));
            return false;
        }

        ESP_LOGI(TAG, "READ PASS sample=%u mode1=0x%02x mode2=0x%02x", sample, mode1, mode2);
        if (sample == 1)
        {
            initial_mode1 = mode1;
            initial_mode2 = mode2;
        }
        else if (mode1 != initial_mode1 || mode2 != initial_mode2)
        {
            ESP_LOGE(TAG, "Mode registers changed between samples");
            return false;
        }

        if (sample < SAMPLE_COUNT)
        {
            vTaskDelay(pdMS_TO_TICKS(1000));
        }
    }
    return true;
}

static bool run_logic_diagnostic(void)
{
    ESP_LOGI(TAG, "BEGIN diagnostic=v1 sda=1 scl=14 hz=100000 address=0x40 outputs=unconnected");
    i2c_master_bus_config_t bus_config = {
        .i2c_port = I2C_NUM_0,
        .sda_io_num = GPIO_NUM_1,
        .scl_io_num = GPIO_NUM_14,
        .clk_source = I2C_CLK_SRC_DEFAULT,
        .glitch_ignore_cnt = 7,
        // The breakout's pull-ups to VCC (3V3) remain the external pull-ups.
        .flags.enable_internal_pullup = true,
    };
    i2c_master_bus_handle_t bus = NULL;
    esp_err_t result = i2c_new_master_bus(&bus_config, &bus);
    if (result != ESP_OK)
    {
        ESP_LOGE(TAG, "BUS FAIL error=%s", esp_err_to_name(result));
        return false;
    }

    // Probe only the expected individual address, never General Call/All Call.
    result = i2c_master_probe(bus, PCA_ADDRESS, TRANSFER_TIMEOUT_MS);
    bool passed = false;
    i2c_master_dev_handle_t device = NULL;
    if (result == ESP_OK)
    {
        ESP_LOGI(TAG, "ACK PASS address=0x40");
        i2c_device_config_t device_config = {
            .dev_addr_length = I2C_ADDR_BIT_LEN_7,
            .device_address = PCA_ADDRESS,
            .scl_speed_hz = 100000,
        };
        result = i2c_master_bus_add_device(bus, &device_config, &device);
        if (result == ESP_OK)
        {
            passed = read_mode_samples(device);
        }
        else
        {
            ESP_LOGE(TAG, "DEVICE FAIL error=%s", esp_err_to_name(result));
        }
    }
    else
    {
        ESP_LOGE(TAG, "ACK FAIL address=0x40 error=%s", esp_err_to_name(result));
    }

    if (device != NULL)
    {
        result = i2c_master_bus_rm_device(device);
        if (result != ESP_OK)
        {
            ESP_LOGE(TAG, "DEVICE CLEANUP FAIL error=%s", esp_err_to_name(result));
            passed = false;
        }
    }
    result = i2c_del_master_bus(bus);
    if (result != ESP_OK)
    {
        ESP_LOGE(TAG, "BUS CLEANUP FAIL error=%s", esp_err_to_name(result));
        passed = false;
    }
    return passed;
}

void app_main(void)
{
    bool passed = run_logic_diagnostic();
    ESP_LOGI(TAG, "RESULT %s", passed ? "PASS" : "FAIL");
    for (;;)
    {
        vTaskDelay(pdMS_TO_TICKS(5000));
        ESP_LOGI(TAG, "ALIVE result=%s", passed ? "PASS" : "FAIL");
    }
}

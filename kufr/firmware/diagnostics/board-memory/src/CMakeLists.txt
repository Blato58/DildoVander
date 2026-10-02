idf_component_register(
    SRCS "main.c"
    INCLUDE_DIRS "."
    REQUIRES esp_psram esp_hw_support spi_flash heap freertos log esp_system
)


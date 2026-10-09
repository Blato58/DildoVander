idf_component_register(
    SRCS "main.cpp"
         "../../../src/features/backend_access/NetworkSettings.cpp"
         "../../../src/features/provisioning/DeviceIdentity.cpp"
         "../../../src/features/provisioning/EspIdfProvisioningStorage.cpp"
    INCLUDE_DIRS "." "../../../src"
    REQUIRES esp_driver_usb_serial_jtag esp_timer nvs_flash mbedtls espressif__cjson)

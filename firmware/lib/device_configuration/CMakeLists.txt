idf_component_register(
    SRCS
        "NetworkSettings.cpp"
        "DeviceIdentity.cpp"
        "EspIdfProvisioningStorage.cpp"
    INCLUDE_DIRS "include"
    REQUIRES nvs_flash
)

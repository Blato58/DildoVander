idf_component_register(
    SRCS
        "espidf_main.cpp"
        "features/backend_access/DeviceTokenSession.cpp"
        "features/backend_access/EspIdfTokenExchangeTransport.cpp"
        "features/backend_access/TokenResponseParser.cpp"
        "features/device_control/EspIdfJournalStorage.cpp"
        "features/device_control/PersistentCommandJournal.cpp"
        "features/provisioning/DeviceIdentity.cpp"
        "features/provisioning/EspIdfProvisioningStorage.cpp"
        "features/safety/SafetyController.cpp"
        "features/wand_selection/SelectionSequence.cpp"
    INCLUDE_DIRS "."
    REQUIRES esp_http_client esp_timer freertos log mbedtls nvs_flash
)

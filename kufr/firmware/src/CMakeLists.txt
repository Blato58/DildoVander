idf_component_register(
    SRCS
        "espidf_main.cpp"
        "features/backend_access/BuildDate.cpp"
        "features/backend_access/DeviceTokenSession.cpp"
        "features/backend_access/EspIdfBackendNetwork.cpp"
        "features/backend_access/EspIdfNetworkSettings.cpp"
        "features/backend_access/EspIdfTokenExchangeTransport.cpp"
        "features/backend_access/EspIdfTrustedTimeStore.cpp"
        "features/backend_access/EspIdfWifiStation.cpp"
        "features/backend_access/NetworkSettings.cpp"
        "features/backend_access/ReconnectPolicy.cpp"
        "features/backend_access/TokenResponseParser.cpp"
        "features/backend_access/TrustedTime.cpp"
        "features/device_control/EspIdfJournalStorage.cpp"
        "features/device_control/PersistentCommandJournal.cpp"
        "features/provisioning/DeviceIdentity.cpp"
        "features/provisioning/EspIdfProvisioningStorage.cpp"
        "features/safety/SafetyController.cpp"
        "features/wand_selection/SelectionSequence.cpp"
    INCLUDE_DIRS "."
    REQUIRES esp_event esp_http_client esp_hw_support esp_netif esp_timer esp_wifi freertos log mbedtls nvs_flash
)

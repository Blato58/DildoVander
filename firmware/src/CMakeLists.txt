get_filename_component(sdkconfig_name "${SDKCONFIG}" NAME)
string(REGEX REPLACE "^sdkconfig\\." "" KUFR_PLATFORMIO_ENV "${sdkconfig_name}")

if(KUFR_PLATFORMIO_ENV STREQUAL "production")
idf_component_register(
    SRCS
        "features/network_provisioning/NetworkProfiles.cpp"
        "features/network_provisioning/EspIdfNetworkProfileStorage.cpp"
        "features/network_provisioning/EspIdfBleProvisioning.cpp"
        "features/firmware_update/OtaManifest.cpp"
        "features/firmware_update/OtaPolicy.cpp"
        "features/firmware_update/EspIdfFirmwareUpdate.cpp"
        "features/sd_assets/AssetReader.cpp"
        "features/sd_assets/EspIdfSdAssets.cpp"
        "features/dance/DanceAssets.cpp"
        "features/dance/DanceCoordinator.cpp"
        "features/dance/EspIdfDancePlayer.cpp"
        "features/costume_photo/CostumePhoto.cpp"
        "features/costume_photo/EspIdfCostumeCamera.cpp"
        "features/costume_photo/EspIdfCostumeUpload.cpp"
        "production/main.cpp"
        "features/physical_effects/PhysicalEffects.cpp"
        "features/physical_effects/PhysicalPresentation.cpp"
        "features/physical_effects/PresentationDiagnostics.cpp"
        "features/scene_intent/SceneIntentBridge.cpp"
        "features/offline_show/OfflineShow.cpp"
        "features/offline_show/ImaAdpcm.cpp"
        "features/offline_show/EspIdfOfflineShow.cpp"
        "features/show_cues/DeviceShowAudio.cpp"
        "features/show_cues/DeviceShowCue.cpp"
        "features/show_cues/DeviceShowRuntime.cpp"
        "features/show_cues/EspIdfDeviceShowTransport.cpp"
        "features/physical_effects/EspIdfPhysicalIo.cpp"
        "features/voice_turn/EspIdfVoiceAudio.cpp"
        "features/voice_turn/EspIdfRealtimeTransport.cpp"
        "features/voice_turn/RealtimeEventCodec.cpp"
        "features/voice_turn/RealtimeSessionEngine.cpp"
        "features/voice_turn/VoiceWorkflow.cpp"
        "features/backend_access/BuildDate.cpp"
        "features/backend_access/DeviceTokenSession.cpp"
        "features/backend_access/EspIdfBackendNetwork.cpp"
        "features/keepsake/EspIdfKeepsakeNotifier.cpp"
        "features/backend_access/EspIdfNetworkSettings.cpp"
        "features/backend_access/EspIdfTokenExchangeTransport.cpp"
        "features/backend_access/EspIdfTrustedTimeStore.cpp"
        "features/backend_access/EspIdfWifiStation.cpp"
        "features/backend_access/EspIdfStationNetwork.cpp"
        "features/backend_access/ReconnectPolicy.cpp"
        "features/backend_access/TokenResponseParser.cpp"
        "features/backend_access/TrustedTime.cpp"
        "features/conversation/ConversationStartCodec.cpp"
        "features/conversation/ConversationStartCoordinator.cpp"
        "features/conversation/ConversationRuntimeState.cpp"
        "features/conversation/ConversationReplyInbox.cpp"
        "features/conversation/ConversationSession.cpp"
        "features/conversation/ConversationTextCodec.cpp"
        "features/conversation/ConversationTextInbox.cpp"
        "features/conversation/ConversationTurnFailureInbox.cpp"
        "features/conversation/ConversationUuid.cpp"
        "features/conversation/EspIdfConversationStartTransport.cpp"
        "features/conversation/EspIdfConversationTextTransport.cpp"
        "features/conversation/EspIdfConversationUuid.cpp"
        "features/conversation/EspIdfTurnPollTransport.cpp"
        "features/conversation/OperationAcceptedCodec.cpp"
        "features/conversation/PendingProposal.cpp"
        "features/conversation/TurnPollGuard.cpp"
        "features/conversation/TurnPollSchedule.cpp"
        "features/conversation/TurnOperationCodec.cpp"
        "features/conversation/SpeechOperationCodec.cpp"
        "features/conversation/SpeechAudioBuffer.cpp"
        "features/conversation/EspIdfSpeechTransport.cpp"
        "features/conversation/SpeechOutputCoordinator.cpp"
        "features/device_control/EspIdfJournalStorage.cpp"
        "features/twin_telemetry/EspIdfTwinTelemetry.cpp"
        "features/device_control/DeviceControl.cpp"
        "features/device_control/DeviceControlRuntime.cpp"
        "features/device_control/PersistentCommandJournal.cpp"
        "features/local_control/LocalControlSession.cpp"
        "features/local_control/LocalControlCodec.cpp"
        "features/local_control/LocalControlInbox.cpp"
        "features/local_control/LocalControlOwner.cpp"
        "features/local_control/LocalControlOutput.cpp"
        "features/local_control/EspIdfLocalControlServer.cpp"
        "features/local_control/EspIdfLocalServiceAdvertisement.cpp"
        "features/local_control/LocalBearerAuthenticator.cpp"
        "features/local_control/LocalConversationGateway.cpp"
        "features/local_control/LocalSocketAdmission.cpp"
        "features/provisioning/EspIdfLocalPairingStorage.cpp"
        "features/provisioning/EspIdfPairingRandom.cpp"
        "features/provisioning/EspIdfLocalTlsStorage.cpp"
        "features/provisioning/EspIdfLocalTlsValidator.cpp"
        "features/provisioning/EspIdfLocalTlsGenerator.cpp"
        "features/provisioning/LocalTlsIdentity.cpp"
        "features/provisioning/EspIdfSha256.cpp"
        "features/provisioning/LocalPairingCredential.cpp"
        "features/safety/SafetyController.cpp"
        "features/wand_selection/SelectionSequence.cpp"
        "features/wand_presentation/WandPresentation.cpp"
    INCLUDE_DIRS "."
    REQUIRES app_update protocomm cjson fatfs sdmmc esp_driver_sdmmc esp_driver_i2c esp_driver_rmt esp_driver_i2s esp_driver_gpio bootloader_support esp_event esp_http_client esp_http_server esp_https_server esp_hw_support esp_netif esp_timer esp_wifi freertos log mbedtls mdns nvs_flash device_configuration
)

target_compile_definitions(${COMPONENT_LIB} PRIVATE
    KUFR_FIRMWARE_VERSION="${PROJECT_VER}" KUFR_FIRMWARE_BUILD_ID="${KUFR_BUILD_ID}")
elseif(KUFR_PLATFORMIO_ENV STREQUAL "diag-board-memory")
    idf_component_register(
        SRCS "diagnostics/board-memory/main.c"
        INCLUDE_DIRS "."
        REQUIRES esp_psram esp_hw_support spi_flash heap freertos log esp_system
    )
elseif(KUFR_PLATFORMIO_ENV STREQUAL "diag-hardware-service")
    idf_component_register(
        SRCS "diagnostics/hardware-service/main.cpp"
        INCLUDE_DIRS "." "diagnostics/hardware-service"
        REQUIRES nvs_flash log
    )
elseif(KUFR_PLATFORMIO_ENV STREQUAL "diag-network-service")
    idf_component_register(
        SRCS "diagnostics/network-service/main.cpp"
        INCLUDE_DIRS "."
        REQUIRES esp_driver_usb_serial_jtag esp_timer nvs_flash mbedtls espressif__cjson device_configuration
    )
elseif(KUFR_PLATFORMIO_ENV STREQUAL "diag-pca9685-logic")
    idf_component_register(
        SRCS "diagnostics/pca9685-logic/main.c"
        INCLUDE_DIRS "."
        REQUIRES esp_driver_i2c freertos log
    )
elseif(KUFR_PLATFORMIO_ENV STREQUAL "diag-servo-bench")
    idf_component_register(
        SRCS "diagnostics/servo-bench/main.cpp"
        INCLUDE_DIRS "."
        REQUIRES esp_driver_i2c esp_driver_uart esp_timer freertos
    )
else()
    message(FATAL_ERROR "No source set for PlatformIO environment: ${KUFR_PLATFORMIO_ENV}")
endif()

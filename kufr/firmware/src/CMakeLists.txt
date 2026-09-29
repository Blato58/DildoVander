idf_component_register(
    SRCS
        "espidf_main.cpp"
        "features/safety/SafetyController.cpp"
        "features/wand_selection/SelectionSequence.cpp"
    INCLUDE_DIRS "."
    REQUIRES esp_timer freertos log
)

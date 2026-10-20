cmake_minimum_required(VERSION 3.16.0)

get_filename_component(sdkconfig_name "${SDKCONFIG}" NAME)
string(REGEX REPLACE "^sdkconfig\\." "" KUFR_PLATFORMIO_ENV "${sdkconfig_name}")

set(EXTRA_COMPONENT_DIRS
    "${CMAKE_CURRENT_LIST_DIR}/lib/device_configuration"
    ${EXTRA_COMPONENT_DIRS})
set(COMPONENTS src __pio_env)

if(KUFR_PLATFORMIO_ENV STREQUAL "production")
    set(SDKCONFIG_DEFAULTS "${CMAKE_CURRENT_LIST_DIR}/sdkconfig.defaults")
    find_package(Python3 REQUIRED COMPONENTS Interpreter)
    execute_process(
        COMMAND "${Python3_EXECUTABLE}" "${CMAKE_CURRENT_LIST_DIR}/../mechanika/final/generate_effects.py" --check
        RESULT_VARIABLE score_result)
    if(NOT score_result EQUAL 0)
        message(FATAL_ERROR "Presentation bundle is stale or invalid; run generate_effects.py and review its outputs")
    endif()
    set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS
        "${CMAKE_CURRENT_LIST_DIR}/../mechanika/final/effects.json"
        "${CMAKE_CURRENT_LIST_DIR}/../mechanika/final/effects.schema.json"
        "${CMAKE_CURRENT_LIST_DIR}/../mechanika/final/generate_effects.py"
        "${CMAKE_CURRENT_LIST_DIR}/src/features/physical_effects/PresentationScore.h")
    set(version_arguments --local)
    if(DEFINED ENV{GITHUB_ACTIONS} AND "$ENV{GITHUB_ACTIONS}" STREQUAL "true")
        set(version_arguments --run-number "$ENV{GITHUB_RUN_NUMBER}")
    endif()
    execute_process(
        COMMAND "${Python3_EXECUTABLE}" "${CMAKE_CURRENT_LIST_DIR}/tools/firmware_version.py"
            --policy "${CMAKE_CURRENT_LIST_DIR}/version.txt" ${version_arguments}
        RESULT_VARIABLE version_result OUTPUT_VARIABLE PROJECT_VER OUTPUT_STRIP_TRAILING_WHITESPACE)
    if(NOT version_result EQUAL 0)
        message(FATAL_ERROR "Unable to resolve firmware version")
    endif()
    set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS
        "${CMAKE_CURRENT_LIST_DIR}/version.txt"
        "${CMAKE_CURRENT_LIST_DIR}/tools/firmware_version.py")
    execute_process(COMMAND git rev-parse HEAD
        WORKING_DIRECTORY "${CMAKE_CURRENT_LIST_DIR}"
        OUTPUT_VARIABLE KUFR_BUILD_ID OUTPUT_STRIP_TRAILING_WHITESPACE)
    if(NOT KUFR_BUILD_ID MATCHES "^[0-9a-f]+$" OR NOT KUFR_BUILD_ID)
        message(FATAL_ERROR "Firmware requires a Git build identity")
    endif()
elseif(KUFR_PLATFORMIO_ENV MATCHES "^diag-(board-memory|hardware-service|network-service|pca9685-logic|servo-bench)$")
    string(REGEX REPLACE "^diag-" "" diagnostic_name "${KUFR_PLATFORMIO_ENV}")
    set(SDKCONFIG_DEFAULTS
        "${CMAKE_CURRENT_LIST_DIR}/diagnostics/${diagnostic_name}/sdkconfig.defaults")
else()
    message(FATAL_ERROR "Unsupported PlatformIO environment from SDKCONFIG: ${SDKCONFIG}")
endif()

include($ENV{IDF_PATH}/tools/cmake/project.cmake)
if(KUFR_PLATFORMIO_ENV STREQUAL "production")
    project(kufr_firmware)
else()
    string(REPLACE "-" "_" project_suffix "${KUFR_PLATFORMIO_ENV}")
    project(kufr_${project_suffix})
endif()

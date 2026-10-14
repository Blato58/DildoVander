cmake_minimum_required(VERSION 3.16.0)

find_package(Python3 REQUIRED COMPONENTS Interpreter)
# The firmware and observer must consume exactly the checked-in compiler output.
# Authoring/generation happens explicitly; a build never silently rewrites it.
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
    "${CMAKE_CURRENT_LIST_DIR}/version.txt" "${CMAKE_CURRENT_LIST_DIR}/tools/firmware_version.py")
include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(kufr_firmware)

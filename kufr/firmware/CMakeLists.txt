cmake_minimum_required(VERSION 3.16.0)

file(STRINGS "${CMAKE_CURRENT_LIST_DIR}/version.txt" PROJECT_VER LIMIT_COUNT 1)
if(NOT PROJECT_VER MATCHES "^[0-9]+\\.[0-9]+\\.[0-9]+$")
    message(FATAL_ERROR "version.txt must contain a semantic firmware version")
endif()
include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(kufr_firmware)

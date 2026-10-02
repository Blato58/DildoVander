cmake_minimum_required(VERSION 3.16.0)

# PlatformIO needs its synthetic component to discover compiler settings.
set(COMPONENTS src __pio_env)
include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(kufr_board_memory_diagnostic)

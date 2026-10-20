cmake_minimum_required(VERSION 3.16.0)

# PlatformIO resolves the partition table relative to the open project root.
# ESP-IDF keeps using firmware/ for its dependency lock and managed components.
configure_file(firmware/partitions_16mb.csv
    "${CMAKE_CURRENT_LIST_DIR}/partitions_16mb.csv" COPYONLY)
include(firmware/CMakeLists.txt)

# CMake's top-level scan requires a literal project() command even though the
# included entry point already calls it. Never initialize host compilers first.
if(NOT CMAKE_PROJECT_NAME)
    project(kufr_workspace NONE)
endif()

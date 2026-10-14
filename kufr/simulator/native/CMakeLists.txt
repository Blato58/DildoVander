cmake_minimum_required(VERSION 3.16)
project(kufr_native_twin LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 14)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(FEATURES "${CMAKE_CURRENT_SOURCE_DIR}/../../firmware/src/features")
add_executable(kufr-native-twin
  main.cpp VirtualHardware.cpp FileJournal.cpp
  ${FEATURES}/safety/SafetyController.cpp
  ${FEATURES}/wand_selection/SelectionSequence.cpp
  ${FEATURES}/device_control/DeviceControl.cpp
  ${FEATURES}/device_control/DeviceControlRuntime.cpp
  ${FEATURES}/device_control/PersistentCommandJournal.cpp
  ${FEATURES}/conversation/ConversationRuntimeState.cpp
  ${FEATURES}/conversation/PendingProposal.cpp
  ${FEATURES}/conversation/TurnPollGuard.cpp
  ${FEATURES}/conversation/ConversationTextCodec.cpp
  ${FEATURES}/conversation/ConversationStartCodec.cpp
  ${FEATURES}/conversation/ConversationSession.cpp
  ${FEATURES}/conversation/TurnOperationCodec.cpp
  ${FEATURES}/wand_presentation/WandPresentation.cpp
  ${FEATURES}/physical_effects/PhysicalEffects.cpp
  ${FEATURES}/physical_effects/PhysicalPresentation.cpp
  ${FEATURES}/physical_effects/PresentationDiagnostics.cpp
  ${FEATURES}/dance/DanceCoordinator.cpp)
target_include_directories(kufr-native-twin PRIVATE ${FEATURES})
if(MSVC)
  target_compile_options(kufr-native-twin PRIVATE /W4 /WX /wd4244 /utf-8)
else()
  target_compile_options(kufr-native-twin PRIVATE -Wall -Wextra -Werror)
  find_package(Threads REQUIRED)
  target_link_libraries(kufr-native-twin PRIVATE Threads::Threads)
endif()

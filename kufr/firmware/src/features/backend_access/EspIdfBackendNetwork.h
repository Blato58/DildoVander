#pragma once

#include <atomic>

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

#include "NetworkSettings.h"
#include "../conversation/ConversationRuntimeState.h"
#include "../conversation/ConversationTextInbox.h"
#include "../provisioning/DeviceIdentity.h"

class EspIdfBackendNetwork final {
 public:
  EspIdfBackendNetwork(DeviceIdentity& identity,
                       ConversationRuntimeState& runtimeState);
  bool Start(bool nvsInitialized);
  bool QueueTextTurn(const std::string& text);

 private:
  static void TaskEntry(void* argument);
  void Run();
  void RunTextTurn(const std::string& text, uint64_t snapshotVersion,
                   class DeviceTokenSession& tokens,
                   class EspIdfWifiStation& wifi,
                   class ConversationStartCoordinator& coordinator,
                   struct ConversationStartRequest& startRequest);

  DeviceIdentity& identity_;
  ConversationRuntimeState& runtimeState_;
  NetworkSettings settings_;
  ConversationTextInbox textInbox_;
  std::atomic<bool> online_{false};
  std::string bootId_;
  uint64_t startSnapshotVersion_ = 0;
  TaskHandle_t task_ = nullptr;
};

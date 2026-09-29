#include <stdint.h>

#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "nvs_flash.h"

#include "features/device_control/EspIdfJournalStorage.h"
#include "features/device_control/DeviceControl.h"
#include "features/device_control/DeviceControlRuntime.h"
#include "features/device_control/PersistentCommandJournal.h"
#include "features/backend_access/EspIdfBackendNetwork.h"
#include "features/conversation/ConversationRuntimeState.h"
#include "features/provisioning/DeviceIdentity.h"
#include "features/provisioning/EspIdfProvisioningStorage.h"
#include "features/safety/SafetyController.h"
#include "features/wand_selection/SelectionSequence.h"

namespace {

constexpr char LogTag[] = "kufr";
constexpr uint32_t ControlLoopPeriodMs = 10;

class LoggingSafetyPort final : public SafetyPort {
 public:
  void DisableActuators() override {
    ESP_LOGI(LogTag, "Actuator disable requested (no GPIO adapter)");
  }

  void StartVibration(uint8_t compartment) override {
    ESP_LOGW(LogTag, "Vibration %u requested without an actuator adapter",
             static_cast<unsigned>(compartment));
  }

  void ReleaseLatch(uint8_t compartment) override {
    ESP_LOGW(LogTag, "Latch %u requested without an actuator adapter",
             static_cast<unsigned>(compartment));
  }
};

void LogStep(const SelectionStep& step) {
  ESP_LOGI(LogTag, "Selection step %u for compartment %u",
           static_cast<unsigned>(step.effect),
           static_cast<unsigned>(step.compartment));
}

}  // namespace

extern "C" void app_main() {
  LoggingSafetyPort safetyPort;
  SafetyController safetyController(safetyPort);
  SelectionSequence selectionSequence(safetyController);

  // Safe output request precedes all future provisioning and network work.
  safetyController.InitializeSafe();
  const esp_err_t nvsStatus = nvs_flash_init();
  EspIdfJournalStorage journalStorage(nvsStatus == ESP_OK);
  PersistentCommandJournal journal(journalStorage);
  EspIdfProvisioningStorage identityStorage(nvsStatus == ESP_OK);
  DeviceIdentity identity(identityStorage);
  if (!journal.IsHealthy()) {
    safetyController.ReportFault();
    ESP_LOGE(LogTag, "Journal unavailable; control remains faulted (%d)",
             static_cast<int>(nvsStatus));
  } else if (journal.HasPending()) {
    ESP_LOGW(LogTag, "Pending selection requires inspection before recovery");
  }
  if (!identity.IsHealthy()) {
    ESP_LOGE(LogTag, "Device identity unavailable; network stays offline");
  } else if (!identity.IsProvisioned()) {
    ESP_LOGW(LogTag, "Device identity not provisioned; network stays offline");
  }
  // No local control transport or calibrated actuator adapter exists yet.
  // Empty boot identity and zero calibration keep command execution disabled.
  // The command result cache is too large for the ESP-IDF app_main stack.
  static DeviceControl deviceControl(identity.DeviceId(), "", selectionSequence,
                                     journal, 0);
  ConversationRuntimeState runtimeState(false, 0, "");
  DeviceControlRuntime controlRuntime(deviceControl, selectionSequence,
                                      runtimeState, "neutral-1");
  controlRuntime.PublishCurrent();
  EspIdfBackendNetwork backendNetwork(identity, runtimeState);
  if (!backendNetwork.Start(nvsStatus == ESP_OK)) {
    ESP_LOGW(LogTag, "Backend network not configured; stays offline");
  }
  ESP_LOGI(LogTag, "ESP-IDF control loop started; actuators are unconnected");

  while (true) {
    const uint32_t nowMs =
        static_cast<uint32_t>(esp_timer_get_time() / 1000);
    SelectionStep step;

    if (controlRuntime.Tick(nowMs, step)) {
      LogStep(step);
    }

    vTaskDelay(pdMS_TO_TICKS(ControlLoopPeriodMs));
  }
}

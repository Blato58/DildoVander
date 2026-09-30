#include <stdint.h>

#include <array>
#include <string>

#include "bootloader_random.h"
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
#include "features/backend_access/EspIdfStationNetwork.h"
#include "features/conversation/ConversationRuntimeState.h"
#include "features/conversation/ConversationUuid.h"
#include "features/conversation/EspIdfConversationUuid.h"
#include "features/local_control/EspIdfLocalControlServer.h"
#include "features/local_control/LocalBearerAuthenticator.h"
#include "features/local_control/LocalControlOutput.h"
#include "features/local_control/LocalControlOwner.h"
#include "features/provisioning/DeviceIdentity.h"
#include "features/provisioning/EspIdfLocalPairingStorage.h"
#include "features/provisioning/EspIdfLocalTlsStorage.h"
#include "features/provisioning/EspIdfLocalTlsValidator.h"
#include "features/provisioning/EspIdfPairingRandom.h"
#include "features/provisioning/EspIdfProvisioningStorage.h"
#include "features/provisioning/EspIdfSha256.h"
#include "features/provisioning/LocalTlsIdentity.h"
#include "features/safety/SafetyController.h"
#include "features/wand_selection/SelectionSequence.h"

namespace {

constexpr char LogTag[] = "kufr";
constexpr uint32_t ControlLoopPeriodMs = 10;
constexpr char CatalogVersion[] = "ollivander-private-party-v1";

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

class RuntimeClock final : public LocalControlClockPort {
 public:
  uint64_t NowMs() const override {
    return static_cast<uint64_t>(esp_timer_get_time() / 1000);
  }
};

class RuntimeLeaseIds final : public LocalControlIdPort {
 public:
  explicit RuntimeLeaseIds(PairingRandomPort& random) : random_(random) {}
  std::string NewLeaseId() override {
    std::array<uint8_t, 32> entropy{};
    const bool generated = random_.Fill(entropy.data(), entropy.size());
    const std::string id = generated ? FormatConversationUuidV4(entropy.data()) : "";
    volatile uint8_t* bytes = entropy.data();
    for (size_t index = 0; index < entropy.size(); ++index) {
      bytes[index] = 0;
    }
    return id;
  }
 private:
  PairingRandomPort& random_;
};

class UnverifiedInspection final : public LocalControlRecoveryGate {
 public:
  bool InspectionAllowsRecovery() const override { return false; }
};

class LoggingSteps final : public LocalControlStepPort {
 public:
  void OnStep(const SelectionStep& step) override { LogStep(step); }
};

}  // namespace

extern "C" void app_main() {
  static LoggingSafetyPort safetyPort;
  static SafetyController safetyController(safetyPort);
  static SelectionSequence selectionSequence(safetyController);

  // Safe output request precedes all future provisioning and network work.
  safetyController.InitializeSafe();
  // RF is not running yet. Temporarily enable the internal entropy source so
  // control and backend share one unpredictable identity for this boot.
  bootloader_random_enable();
  static const std::string bootId = GenerateEspIdfConversationUuid();
  static const std::string conversationId = GenerateEspIdfConversationUuid();
  bootloader_random_disable();
  const esp_err_t nvsStatus = nvs_flash_init();
  static EspIdfJournalStorage journalStorage(nvsStatus == ESP_OK);
  static PersistentCommandJournal journal(journalStorage);
  static EspIdfProvisioningStorage identityStorage(nvsStatus == ESP_OK);
  static DeviceIdentity identity(identityStorage);
  static EspIdfLocalTlsStorage tlsStorage(nvsStatus == ESP_OK);
  static EspIdfLocalTlsValidator tlsValidator;
  static LocalTlsIdentity tlsIdentity(tlsStorage, tlsValidator);
  static EspIdfLocalPairingStorage pairingStorage(nvsStatus == ESP_OK);
  static EspIdfSha256 hasher;
  static EspIdfPairingRandom random;
  static LocalPairingCredential pairing(pairingStorage, hasher, random);
  if (!journal.IsHealthy()) {
    safetyController.ReportFault();
    ESP_LOGE(LogTag, "Journal unavailable; control remains faulted (%d)",
             static_cast<int>(nvsStatus));
  } else if (journal.HasPending()) {
    ESP_LOGW(LogTag, "Pending selection requires inspection before recovery");
  }
  if (!identity.IsHealthy()) {
    ESP_LOGE(LogTag, "Device identity unavailable; backend stays offline");
  } else if (!identity.IsProvisioned()) {
    ESP_LOGW(LogTag, "Device identity not provisioned; backend stays offline");
  }
  // B1 has no calibrated physical port/inspection gate. Do not fake recovery.
  // Boot-lifetime objects also keep the small app_main stack bounded.
  static DeviceControl deviceControl(
      identity.DeviceId(), bootId, selectionSequence, journal, 0);
  static ConversationRuntimeState runtimeState(false, 0, "");
  static DeviceControlRuntime controlRuntime(
      deviceControl, selectionSequence, runtimeState, CatalogVersion);
  controlRuntime.PublishCurrent();
  static ConversationSession conversation(bootId, conversationId);
  static EspIdfStationNetwork station;
  const bool stationStarted = station.Start(nvsStatus == ESP_OK);
  static EspIdfBackendNetwork backendNetwork(identity, runtimeState, bootId, station);
  if (!stationStarted || !backendNetwork.Start(nvsStatus == ESP_OK)) {
    ESP_LOGW(LogTag, "Backend network not configured; stays offline");
  }
  static LocalBearerAuthenticator authenticator(pairing);
  static LocalSocketAdmission admission(authenticator);
  static LocalControlInbox inbox(identity.DeviceId(), bootId);
  static EspIdfLocalControlServer server(tlsIdentity, pairing, admission, inbox);
  static LocalControlOutput output(
      identity.DeviceId(), bootId, deviceControl, journal, safetyController,
      selectionSequence, conversation, server);
  static RuntimeClock clock;
  static RuntimeLeaseIds leaseIds(random);
  static UnverifiedInspection inspection;
  static LoggingSteps steps;
  static LocalControlOwner owner(
      identity.DeviceId(), bootId, controlRuntime, runtimeState, conversation,
      backendNetwork, inbox, server, output, leaseIds, inspection, steps, clock);
  bool serverStarted = false;
  uint64_t retryServerAtMs = 0;
  ESP_LOGI(LogTag, "ESP-IDF control loop started; actuators are unconnected");

  while (true) {
    const uint64_t nowMs = clock.NowMs();
    if (!serverStarted &&
        nowMs >= retryServerAtMs &&
        station.HasIp() &&
        identity.IsHealthy() && identity.IsProvisioned() &&
        tlsIdentity.IsHealthy() && tlsIdentity.IsProvisioned() &&
        pairing.IsHealthy() &&
        (pairing.IsPairingReady() || pairing.IsPaired())) {
      serverStarted = server.Start();
      retryServerAtMs = nowMs + 5000;
      if (serverStarted) {
        ESP_LOGI(LogTag, "Provisioned local TLS control started; hardware stays gated");
      }
    }
    server.Pump();
    owner.Tick();
    owner.PumpReply(backendNetwork);
    vTaskDelay(pdMS_TO_TICKS(ControlLoopPeriodMs));
  }
}

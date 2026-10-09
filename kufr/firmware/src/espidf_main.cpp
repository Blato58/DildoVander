#include <stdint.h>

#include <array>
#include <string>
#include <mutex>

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
#include "features/local_control/EspIdfLocalServiceAdvertisement.h"
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
#include "features/physical_effects/EspIdfPhysicalIo.h"
#include "features/physical_effects/PhysicalPresentation.h"
#include "features/network_provisioning/EspIdfBleProvisioning.h"
#include "features/firmware_update/EspIdfFirmwareUpdate.h"
#include "features/sd_assets/EspIdfSdAssets.h"

namespace {

constexpr char LogTag[] = "kufr";
constexpr uint32_t ControlLoopPeriodMs = 10;
constexpr char CatalogVersion[] = "ollivander-private-party-v1";

// Owner admissions and a worker's idle reservation share this mutex. Sampling
// an idle flag without holding it would race a queued voice/servo start.
struct MaintenanceGate {
  std::mutex mutex;
  bool safeIdle = true;
  bool networkHeld = false;
  bool otaHeld = false;
  static bool AcquireNetwork(void* context) {
    auto& gate = *static_cast<MaintenanceGate*>(context);
    std::unique_lock<std::mutex> lock(gate.mutex, std::try_to_lock);
    if (!lock.owns_lock() || !gate.safeIdle || gate.networkHeld || gate.otaHeld) return false;
    gate.networkHeld = true;
    return true;
  }
  static void ReleaseNetwork(void* context) {
    auto& gate = *static_cast<MaintenanceGate*>(context);
    std::lock_guard<std::mutex> lock(gate.mutex);
    gate.networkHeld = false;
  }
};

struct BleStatusBridge {
  kufr::ota::EspIdfFirmwareUpdate& ota;
  EspIdfBackendNetwork& backend;
  static void Status(cJSON* response, void* context) {
    auto& bridge = *static_cast<BleStatusBridge*>(context);
    cJSON_AddBoolToObject(response, "b", bridge.backend.IsOnline());
    const auto status = bridge.ota.Status();
    cJSON* update = cJSON_AddObjectToObject(response, "o");
    if (!update) return;
    cJSON_AddStringToObject(update, "s", status.state.c_str());
    cJSON_AddStringToObject(update, "a", status.availableVersion.c_str());
    cJSON_AddStringToObject(update, "e", status.error.c_str());
    cJSON_AddBoolToObject(update, "i", status.automaticInstall);
  }
  static bool Command(const char* name, bool automaticInstall, void* context) {
    auto& bridge = *static_cast<BleStatusBridge*>(context);
    if (std::string(name) == "ota-check") return bridge.ota.RequestCheck();
    if (std::string(name) == "ota-install") return bridge.ota.RequestInstall();
    if (std::string(name) == "ota-policy") return bridge.ota.SetAutomaticInstall(automaticInstall);
    return false;
  }
};

class HardwareSafetyPort final : public SafetyPort {
 public:
  explicit HardwareSafetyPort(PhysicalEffects& effects) : effects_(effects) {}
  void DisableActuators() override { effects_.Disable(); }
  void StartVibration(uint8_t compartment) override {
    if (!effects_.StartVibration(compartment)) effects_.Fault();
  }
  void ReleaseLatch(uint8_t compartment) override {
    if (!effects_.Move(compartment, true)) effects_.Fault();
  }
  void LowerProp(uint8_t compartment) override {
    if (!effects_.Move(compartment, false)) effects_.Fault();
  }
  uint32_t MotionTimeoutMs(uint8_t n) const override { return effects_.MotionTimeoutMs(n); }
  bool PropRaised(uint8_t compartment) const override { return effects_.IsRaised(compartment); }
  bool PropResting(uint8_t compartment) const override { return effects_.IsResting(compartment); }
 private:
  PhysicalEffects& effects_;
};

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

class PhysicalInspection final : public LocalControlRecoveryGate {
 public:
  explicit PhysicalInspection(PhysicalEffects& effects) : effects_(effects) {}
  bool InspectionAllowsRecovery() const override {
    if (!effects_.Healthy() || !effects_.ButtonPressed()) return false;
    return true;
  }
 private:
  PhysicalEffects& effects_;
};

class HardwareSteps final : public LocalControlStepPort {
 public:
  HardwareSteps(PhysicalEffects& effects, DeviceControlRuntime& runtime) : effects_(effects), runtime_(runtime) {}
  void OnStep(const SelectionStep& step) override {
    switch (step.effect) {
      case SelectionEffect::SearchStep:
        effects_.Highlight(step.compartment, 100, 40, 255); break;
      case SelectionEffect::Highlight:
        effects_.Highlight(step.compartment, 255, 170, 30); break;
      case SelectionEffect::Stopped:
        effects_.Highlight(0, 0, 0, 0); break;
      case SelectionEffect::Idle:
        if (!runtime_.HasAutonomousPresentation()) effects_.Highlight(0, 0, 0, 0);
        break;
      default: break;
    }
  }
 private:
  PhysicalEffects& effects_;
  DeviceControlRuntime& runtime_;
};

}  // namespace

extern "C" void app_main() {
  static EspIdfPhysicalIo hardware;
  static PhysicalEffects effects(hardware);
  static HardwareSafetyPort safetyPort(effects);
  static SafetyController safetyController(safetyPort);
  static SelectionSequence selectionSequence(safetyController, true);

  // Safe output request precedes all future provisioning and network work.
  safetyController.InitializeSafe();
  // RF is not running yet. Temporarily enable the internal entropy source so
  // control and backend share one unpredictable identity for this boot.
  bootloader_random_enable();
  static const std::string bootId = GenerateEspIdfConversationUuid();
  static const std::string conversationId = GenerateEspIdfConversationUuid();
  bootloader_random_disable();
  const esp_err_t nvsStatus = nvs_flash_init();
  const bool hardwareStarted = hardware.Initialize(nvsStatus == ESP_OK);
  std::array<ServoCalibration, 6> calibration{};
  uint64_t calibrationRevision = 0;
  const bool calibrated = hardwareStarted && hardware.LoadCalibration(calibration, calibrationRevision) &&
                          effects.Configure(calibration, calibrationRevision);
  if (!calibrated) ESP_LOGW(LogTag, "Hardware wiring/calibration unverified; motion stays disabled");
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
  // Hardware validation is provisioned after measurement, never by network input.
  // Boot-lifetime objects also keep the small app_main stack bounded.
  static DeviceControl deviceControl(
      identity.DeviceId(), bootId, selectionSequence, journal, effects.CalibrationRevision());
  static ConversationRuntimeState runtimeState(false, 0, "");
  static DeviceControlRuntime controlRuntime(
      deviceControl, selectionSequence, runtimeState, CatalogVersion);
  static PhysicalPresentation presentationPort(effects, controlRuntime);
  static WandPresentation presentation(presentationPort);
  controlRuntime.BindPresentation(presentation);
  controlRuntime.PublishCurrent();
  static ConversationSession conversation(bootId, conversationId);
  static MaintenanceGate maintenance;
  static EspIdfStationNetwork station;
  station.SetChangeGate({MaintenanceGate::AcquireNetwork, MaintenanceGate::ReleaseNetwork, &maintenance});
  const bool stationStarted = station.Start(nvsStatus == ESP_OK);
  static EspIdfBackendNetwork backendNetwork(identity, runtimeState, bootId, station);
  backendNetwork.InitializeVoice(nvsStatus == ESP_OK, hardware.ProfileSelected() ? &hardware : nullptr);
  backendNetwork.InitializeCamera(nvsStatus == ESP_OK, hardware.Available());
  if (!stationStarted || !backendNetwork.Start(nvsStatus == ESP_OK)) {
    ESP_LOGW(LogTag, "Backend network not configured; stays offline");
  }
  static kufr::ota::EspIdfFirmwareUpdate firmwareUpdate;
  firmwareUpdate.Begin();
  static kufr::assets::EspIdfSdAssets sdAssets;
  sdAssets.Begin();
  static BleStatusBridge bleBridge{firmwareUpdate, backendNetwork};
  static EspIdfBleProvisioning provisioning(station);
  provisioning.Begin(nvsStatus == ESP_OK, identity.DeviceId().c_str(), KUFR_FIRMWARE_VERSION,
      {BleStatusBridge::Status, BleStatusBridge::Command, &bleBridge});
  static LocalBearerAuthenticator authenticator(pairing);
  static LocalSocketAdmission admission(authenticator);
  static LocalControlInbox inbox(identity.DeviceId(), bootId);
  static EspIdfLocalControlServer server(tlsIdentity, pairing, admission, inbox);
  static EspIdfLocalServiceAdvertisement advertisement(station, hasher);
  static LocalControlOutput output(
      identity.DeviceId(), bootId, deviceControl, journal, safetyController,
      selectionSequence, conversation, server, &backendNetwork);
  static RuntimeClock clock;
  static RuntimeLeaseIds leaseIds(random);
  static PhysicalInspection inspection(effects);
  static HardwareSteps steps(effects, controlRuntime);
  static LocalControlOwner owner(
      identity.DeviceId(), bootId, controlRuntime, runtimeState, conversation,
      backendNetwork, inbox, server, output, leaseIds, inspection, steps, clock, &backendNetwork);
  bool serverStarted = false;
  uint64_t retryServerAtMs = 0;
  bool advertisementStarted = false;
  uint64_t retryAdvertisementAtMs = 0;
  bool rawButton = false, button = false, gestureConsumed = false;
  bool hardwareFaultHandled = false;
  uint64_t buttonChangedAt = 0, buttonPressedAt = 0;
  ESP_LOGI(LogTag, "ESP-IDF control loop started; physical outputs require verified service configuration");

  while (true) {
    std::unique_lock<std::mutex> maintenanceLock(maintenance.mutex);
    const uint64_t nowMs = clock.NowMs();
    TurnProposal pendingProposal;
    maintenance.safeIdle = !deviceControl.HasManualControl() && !selectionSequence.IsBusy() &&
        !controlRuntime.HasAutonomousPresentation() && !conversation.HasActiveTurn() &&
        !journal.HasPending() && !backendNetwork.IsVoiceBusy() &&
        !backendNetwork.ReadPendingProposal(pendingProposal, nowMs);
    const bool selfTestReady = nvsStatus == ESP_OK && journal.IsHealthy() && stationStarted &&
        (!hardware.ProfileSelected() || hardwareStarted);
    firmwareUpdate.Tick(nowMs, station.HasIp(), selfTestReady,
        maintenance.safeIdle && !maintenance.networkHeld && !sdAssets.Reserved());
    sdAssets.Tick(maintenance.safeIdle && !maintenance.networkHeld && !firmwareUpdate.Reserved());
    const auto otaStatus = firmwareUpdate.Status();
    // Boot verification freezes new interactions while permitting STA recovery.
    maintenance.otaHeld = sdAssets.Reserved() ||
        (firmwareUpdate.Reserved() && otaStatus.state != "verifying-boot");
    const bool maintaining = maintenance.networkHeld || firmwareUpdate.Reserved() || sdAssets.Reserved();
    owner.SetMaintenance(maintaining);
    provisioning.Tick(nowMs, station.HasIp());
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
    if (serverStarted && !advertisementStarted && nowMs >= retryAdvertisementAtMs) {
      advertisementStarted = advertisement.Start(identity.DeviceId());
      retryAdvertisementAtMs = nowMs + 5000;
    }
    effects.Tick(nowMs);
    if (effects.Healthy()) hardwareFaultHandled = false;
    if (calibrated && !effects.Healthy() && !hardwareFaultHandled) {
      hardwareFaultHandled = true;
      SelectionStep stopped;
      controlRuntime.Stop(stopped);
      backendNetwork.StopVoice();
      steps.OnStep(stopped);
    }
    const bool sampleButton = effects.ButtonPressed();
    if (sampleButton != rawButton) { rawButton = sampleButton; buttonChangedAt = nowMs; }
    if (sampleButton != button && nowMs - buttonChangedAt >= 30) {
      button = sampleButton;
      if (button) {
        buttonPressedAt = nowMs;
        gestureConsumed = false;
        if (controlRuntime.HasAutonomousPresentation() || selectionSequence.IsBusy()) {
          SelectionStep stopped;
          controlRuntime.Stop(stopped);
          backendNetwork.StopVoice();
          steps.OnStep(stopped);
          gestureConsumed = true;
        }
      } else if (!gestureConsumed && !maintaining) {
        const auto voice = backendNetwork.VoiceStatus();
        if (voice.phase != "idle" && voice.phase != "unavailable") backendNetwork.StopVoice();
        else {
          ConversationRuntimeSnapshot snapshot;
          if (runtimeState.Read(snapshot)) {
            if (voice.mode == "off") backendNetwork.ConfigureVoice("gpt-live", "marin");
            backendNetwork.StartVoice(GenerateEspIdfConversationUuid(), snapshot);
          }
        }
      }
    }
    if (!maintaining && button && !gestureConsumed && nowMs - buttonPressedAt >= 2000) {
      gestureConsumed = true;
      if (!selectionSequence.IsBusy() && !controlRuntime.HasAutonomousPresentation() &&
          effects.RecoverAfterInspection()) controlRuntime.RecoverAfterInspection();
    }
    server.Pump();
    owner.Tick();
    inbox.RunIfNoUrgent([&]() { presentationPort.Tick(clock.NowMs()); });
    owner.PumpReply(backendNetwork);
    // Owner has applied urgent Stop/lease changes before any autonomous start.
    inbox.RunIfNoUrgent([&]() {
      if (!maintaining && !deviceControl.HasManualControl() && effects.Healthy())
        backendNetwork.BeginPendingPresentation(presentation, clock.NowMs());
    });
    backendNetwork.FinishPresentation(presentation);
    // Publish idle only after all admissions in this iteration have completed.
    maintenance.safeIdle = !deviceControl.HasManualControl() && !selectionSequence.IsBusy() &&
        !controlRuntime.HasAutonomousPresentation() && !conversation.HasActiveTurn() &&
        !journal.HasPending() && !backendNetwork.IsVoiceBusy() &&
        !backendNetwork.ReadPendingProposal(pendingProposal, clock.NowMs());
    maintenanceLock.unlock();
    vTaskDelay(pdMS_TO_TICKS(ControlLoopPeriodMs));
  }
}

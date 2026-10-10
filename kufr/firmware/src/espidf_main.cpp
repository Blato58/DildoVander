#include <stdint.h>

#include <array>
#include <string>
#include <mutex>

#include "bootloader_random.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/semphr.h"
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
#include "features/provisioning/EspIdfLocalTlsGenerator.h"
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

struct InitialTlsSetup {
  LocalTlsIdentity& identity;
  LocalTlsGenerator& generator;
  SemaphoreHandle_t completed = nullptr;
  bool ready = false;
  static void Run(void* context) {
    auto& setup = *static_cast<InitialTlsSetup*>(context);
    setup.ready = setup.identity.InitializeIfMissing(setup.generator);
    xSemaphoreGive(setup.completed);
    vTaskDelete(nullptr);
  }
  bool Initialize() {
    if (!identity.IsHealthy()) { return false; }
    if (identity.IsProvisioned()) { return true; }
    completed = xSemaphoreCreateBinary();
    if (completed == nullptr) { return false; }
    // Crypto's nested call frames should not consume the 3584-byte main stack.
    const bool started = xTaskCreate(Run, "initial_local_tls", 16384, this, 3, nullptr) == pdPASS;
    if (started) { xSemaphoreTake(completed, portMAX_DELAY); }
    vSemaphoreDelete(completed);
    completed = nullptr;
    return started && ready;
  }
};

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
  DeviceIdentity& identity;
  LocalTlsIdentity& tls;
  LocalPairingCredential& pairing;
  EspIdfStationNetwork& station;
  const std::string& hostname;
  MaintenanceGate& maintenance;
  LocalSocketAdmission& admission;
  LocalControlInbox& inbox;

  const char* PairingPrerequisites(bool recovering) {
    if (!identity.IsHealthy() || !identity.IsProvisioned() ||
        !tls.IsHealthy() || !tls.IsProvisioned() || hostname.empty()) {
      return "pairing_unavailable";
    }
    if (!pairing.IsHealthy()) { return "pairing_storage_unavailable"; }
    // Backend provisioning historically accepts IDs outside control-v1. Do
    // not consume enrollment that the native control client cannot validate.
    if (!EspIdfLocalServiceAdvertisement::SupportsLocalControl(identity.DeviceId())) {
      return "pairing_requires_service";
    }
    if (pairing.IsPairingReady()) { return "pairing_requires_service"; }
    if (recovering && !pairing.IsPaired()) { return "pairing_unavailable"; }
    if (!recovering && pairing.IsPaired()) { return "already_paired"; }
    if (!station.HasIp()) { return "network_not_ready"; }
    return nullptr;
  }

  cJSON* PreparePairingResponse(cJSON* response) {
    // Allocate every response field before changing the durable credential.
    // Token data never enters public status, advertising or firmware storage.
    constexpr char Hex[] = "0123456789abcdef";
    char pin[65]{};
    for (size_t index = 0; index < tls.PublicKeyDigest().size(); ++index) {
      pin[index * 2] = Hex[tls.PublicKeyDigest()[index] >> 4];
      pin[index * 2 + 1] = Hex[tls.PublicKeyDigest()[index] & 15];
    }
    const std::string host = hostname + ".local";
    const std::string placeholder(64, '0');
    auto* tokenField = cJSON_AddStringToObject(response, "token", placeholder.c_str());
    if (tokenField == nullptr ||
        cJSON_AddStringToObject(response, "id", identity.DeviceId().c_str()) == nullptr ||
        cJSON_AddStringToObject(response, "host", host.c_str()) == nullptr ||
        cJSON_AddStringToObject(response, "spkiSha256", pin) == nullptr) {
      return nullptr;
    }
    return tokenField;
  }

  static void CompletePairingResponse(
      cJSON* tokenField, std::array<uint8_t, LocalPairingCredential::TokenSize>& token) {
    constexpr char Hex[] = "0123456789abcdef";
    for (size_t index = 0; index < token.size(); ++index) {
      tokenField->valuestring[index * 2] = Hex[token[index] >> 4];
      tokenField->valuestring[index * 2 + 1] = Hex[token[index] & 15];
    }
    NetworkProfiles::Wipe(token.data(), token.size());
  }

  static const char* PairLocal(cJSON* response, void* context) {
    auto& bridge = *static_cast<BleStatusBridge*>(context);
    const char* error = bridge.PairingPrerequisites(false);
    if (error != nullptr) { return error; }
    auto* tokenField = bridge.PreparePairingResponse(response);
    if (tokenField == nullptr) { return "pairing_failed"; }
    std::array<uint8_t, LocalPairingCredential::TokenSize> token{};
    if (!bridge.pairing.InitializeFromAuthenticatedBle(token)) {
      NetworkProfiles::Wipe(token.data(), token.size());
      return "pairing_failed";
    }
    CompletePairingResponse(tokenField, token);
    return nullptr;
  }

  static const char* RecoverLocal(cJSON* response, void* context) {
    auto& bridge = *static_cast<BleStatusBridge*>(context);
    const char* error = bridge.PairingPrerequisites(true);
    if (error != nullptr) { return error; }
    auto* tokenField = bridge.PreparePairingResponse(response);
    if (tokenField == nullptr) { return "pairing_failed"; }
    std::array<uint8_t, LocalPairingCredential::TokenSize> token{};
    // Owner admissions share this lock; never stop the server, control owner
    // or actuators from BLE. Socket/inbox locks cover pending handshakes and
    // disconnect events as well as live clients. Lock order is maintenance ->
    // admission -> inbox -> credential, compatible with handshake authentication.
    std::unique_lock<std::mutex> lock(bridge.maintenance.mutex, std::try_to_lock);
    if (!lock.owns_lock() || !bridge.maintenance.safeIdle ||
        bridge.maintenance.networkHeld || bridge.maintenance.otaHeld) {
      return "busy";
    }
    bool rotated = false;
    bool disconnected = false;
    const bool empty = bridge.admission.RunIfEmpty([&]() {
      disconnected = bridge.inbox.RunIfDisconnected([&]() {
        rotated = bridge.pairing.RotateFromAuthenticatedBle(token);
      });
    });
    if (!empty || !disconnected || !rotated) {
      NetworkProfiles::Wipe(token.data(), token.size());
      return !empty || !disconnected ? "busy" : "pairing_failed";
    }
    CompletePairingResponse(tokenField, token);
    return nullptr;
  }
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
  // PSA may seed its random generator during parsing or hasher construction.
  // Enable hardware entropy before the first PSA initialization, not just keygen.
  bootloader_random_enable();
  static EspIdfLocalTlsStorage tlsStorage(nvsStatus == ESP_OK);
  static EspIdfLocalTlsValidator tlsValidator;
  static LocalTlsIdentity tlsIdentity(tlsStorage, tlsValidator);
  static EspIdfLocalPairingStorage pairingStorage(nvsStatus == ESP_OK);
  static EspIdfSha256 hasher;
  static EspIdfPairingRandom random;
  static LocalPairingCredential pairing(pairingStorage, hasher, random);
  static const std::string localHostname =
      EspIdfLocalServiceAdvertisement::Hostname(identity.DeviceId(), hasher);
  static EspIdfLocalTlsGenerator tlsGenerator(localHostname + ".local");
  static InitialTlsSetup tlsSetup{tlsIdentity, tlsGenerator};
  // Fresh TLS identity creation is boot-only. Never toggle ADC entropy while
  // the station or audio tasks are running, nor generate inside control ticks.
  const bool localTlsReady = identity.IsHealthy() && identity.IsProvisioned() &&
      !localHostname.empty() && tlsSetup.Initialize();
  bootloader_random_disable();
  if (!localTlsReady) { ESP_LOGW(LogTag, "Local TLS identity unavailable; service recovery required"); }
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
  presentationPort.BindAudio(backendNetwork.PresentationAudio());
  backendNetwork.InitializeCamera(nvsStatus == ESP_OK, hardware.Available());
  if (!stationStarted || !backendNetwork.Start(nvsStatus == ESP_OK)) {
    ESP_LOGW(LogTag, "Backend network not configured; stays offline");
  }
  static kufr::ota::EspIdfFirmwareUpdate firmwareUpdate;
  firmwareUpdate.Begin();
  static kufr::assets::EspIdfSdAssets sdAssets;
  sdAssets.Begin();
  static LocalBearerAuthenticator authenticator(pairing);
  static LocalSocketAdmission admission(authenticator);
  static LocalControlInbox inbox(identity.DeviceId(), bootId);
  static BleStatusBridge bleBridge{
      firmwareUpdate, backendNetwork, identity, tlsIdentity, pairing, station, localHostname,
      maintenance, admission, inbox};
  static EspIdfBleProvisioning provisioning(station);
  provisioning.Begin(nvsStatus == ESP_OK, identity.DeviceId().c_str(), KUFR_FIRMWARE_VERSION,
      {BleStatusBridge::Status, BleStatusBridge::Command, &bleBridge,
       BleStatusBridge::PairLocal, BleStatusBridge::RecoverLocal});
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
  static_assert(CONFIG_ESP_MAIN_TASK_STACK_SIZE >= 16384,
                "TLS identity startup requires at least 16 KiB of main task stack");
  ESP_LOGI(LogTag, "Main task stack: configured %u bytes, minimum remaining %u bytes",
           static_cast<unsigned>(CONFIG_ESP_MAIN_TASK_STACK_SIZE),
           static_cast<unsigned>(uxTaskGetStackHighWaterMark(nullptr)));

  while (true) {
    std::unique_lock<std::mutex> maintenanceLock(maintenance.mutex);
    const uint64_t nowMs = clock.NowMs();
    TurnProposal pendingProposal;
    maintenance.safeIdle = !deviceControl.HasManualControl() && !selectionSequence.IsBusy() &&
        !controlRuntime.HasAutonomousPresentation() && !conversation.HasActiveTurn() &&
        !journal.HasPending() && !backendNetwork.IsVoiceBusy() &&
        !backendNetwork.ReadPendingProposal(pendingProposal, nowMs);
    // The whole-cycle deadline precedes physical output processing, even when
    // per-phase calibration timeouts would still allow the sequence to run.
    presentation.CheckDeadline(nowMs);
    effects.Tick(nowMs);
    const bool selfTestReady = kufr::ota::BootSelfTestReady(
        nvsStatus == ESP_OK && journal.IsHealthy() && stationStarted,
        hardware.ProfileSelected(), calibrated, effects.Healthy());
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
        pairing.IsHealthy()) {
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
    inbox.RunIfNoUrgent([&]() {
      const uint64_t effectNowMs = clock.NowMs();
      presentation.CheckDeadline(effectNowMs);
      presentationPort.Tick(effectNowMs);
    });
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

#include <stdint.h>

#include <array>
#include <cstring>
#include <new>
#include <string>

#include "bootloader_random.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "freertos/semphr.h"
#include "nvs_flash.h"

#include "features/device_control/EspIdfJournalStorage.h"
#include "features/device_control/EspIdfJournalIdentityStorage.h"
#include "features/device_control/JournalIdentityOwner.h"
#include "features/device_control/DeviceControl.h"
#include "features/device_control/DeviceControlRuntime.h"
#include "features/device_control/ControlMaintenanceGate.h"
#include "features/device_control/EspIdfDeviceStorageWorker.h"
#include "features/device_control/PersistentCommandJournal.h"
#include "features/show_diagnostics/ShowTraceMailbox.h"
#include "features/twin_telemetry/EspIdfTwinTelemetry.h"
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
#include "DeviceIdentity.h"
#include "features/provisioning/EspIdfLocalPairingStorage.h"
#include "features/provisioning/EspIdfLocalTlsStorage.h"
#include "features/provisioning/EspIdfLocalTlsValidator.h"
#include "features/provisioning/EspIdfLocalTlsGenerator.h"
#include "features/provisioning/EspIdfPairingRandom.h"
#include "EspIdfProvisioningStorage.h"
#include "features/provisioning/EspIdfSha256.h"
#include "features/provisioning/LocalTlsIdentity.h"
#include "features/safety/SafetyController.h"
#include "features/wand_selection/SelectionSequence.h"
#include "features/physical_effects/EspIdfPhysicalIo.h"
#include "features/physical_effects/PhysicalPresentation.h"
#include "features/dance/DanceCoordinator.h"
#include "features/dance/EspIdfDancePlayer.h"
#include "features/network_provisioning/EspIdfBleProvisioning.h"
#include "features/firmware_update/EspIdfFirmwareUpdate.h"
#include "features/sd_assets/EspIdfSdAssets.h"
#include "features/offline_show/EspIdfOfflineShow.h"
#include "features/offline_show/OfflineEvidenceBridge.h"
#include "features/keepsake/EspIdfKeepsakeInventoryStorage.h"
#include "features/show_cues/DeviceShowRuntime.h"

namespace {

constexpr char LogTag[] = "kufr";
constexpr uint32_t ControlLoopPeriodMs = 10;
constexpr char CatalogVersion[] = "ollivander-private-party-v1";
// All physical admission remains held until complete production-consumer crash
// acceptance. The real storage worker and boot reconciliation are composed now.

class UnavailableOfflineStorage final : public kufr::offline::StoragePort {
 public:
  bool Read(kufr::offline::Outbox&) override { return false; }
  bool Write(const kufr::offline::Outbox&) override { return false; }
};

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

// Logical reservations exclude new admission without holding any mutex through
// a control pass, flash write or worker operation. Stop always keeps its turn.
struct MaintenanceGate {
  ControlMaintenanceGate ownership;
  ControlMaintenanceGate::Ticket network, ota;
  class PairingReservation final {
   public:
    explicit PairingReservation(MaintenanceGate& gate) : gate_(gate) {
      acquired_ = gate_.ownership.Acquire(ControlMaintenanceGate::Owner::Pairing, ticket_);
    }
    ~PairingReservation() {
      if (acquired_) gate_.ownership.Release(ControlMaintenanceGate::Owner::Pairing, ticket_);
    }
    bool Acquired() const { return acquired_; }
   private:
    MaintenanceGate& gate_;
    ControlMaintenanceGate::Ticket ticket_;
    bool acquired_ = false;
  };
  static bool AcquireNetwork(void* context) {
    auto& gate = *static_cast<MaintenanceGate*>(context);
    return gate.ownership.Acquire(ControlMaintenanceGate::Owner::Network, gate.network);
  }
  static void ReleaseNetwork(void* context) {
    auto& gate = *static_cast<MaintenanceGate*>(context);
    gate.ownership.Release(ControlMaintenanceGate::Owner::Network, gate.network);
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
  std::atomic<bool> policyCall{false};
  int pendingAutomaticInstall = -1;

  bool RequestAutomaticInstall(bool enabled) {
    bool expected = false;
    if (!policyCall.compare_exchange_strong(expected, true)) return false;
    bool accepted = false;
    if (pendingAutomaticInstall < 0 || pendingAutomaticInstall == static_cast<int>(enabled)) {
      const bool committed = ota.SetAutomaticInstall(enabled);
      const bool pending = ota.LastOperationIsPending();
      pendingAutomaticInstall = pending ? static_cast<int>(enabled) : -1;
      // BLE command acceptance, like ota-check/install, acknowledges the queue.
      // The status policy remains the actual committed value until its ACK.
      accepted = committed || pending;
    }
    policyCall.store(false);
    return accepted;
  }

  void PollAutomaticInstall() {
    bool expected = false;
    if (!policyCall.compare_exchange_strong(expected, true)) return;
    if (pendingAutomaticInstall >= 0) {
      ota.SetAutomaticInstall(pendingAutomaticInstall != 0);
      if (!ota.LastOperationIsPending()) pendingAutomaticInstall = -1;
    }
    policyCall.store(false);
  }

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
    MaintenanceGate::PairingReservation reservation(bridge.maintenance);
    if (!reservation.Acquired()) return "busy";
    const char* error = bridge.PairingPrerequisites(false);
    if (error != nullptr) { return error; }
    auto* tokenField = bridge.PreparePairingResponse(response);
    if (tokenField == nullptr) { return "pairing_failed"; }
    std::array<uint8_t, LocalPairingCredential::TokenSize> token{};
    bool initialized = false, disconnected = false;
    const bool empty = bridge.admission.RunIfEmpty([&]() {
      disconnected = bridge.inbox.RunIfDisconnected([&]() {
        initialized = bridge.pairing.InitializeFromAuthenticatedBle(token);
      });
    });
    if (!empty || !disconnected || !initialized) {
      NetworkProfiles::Wipe(token.data(), token.size());
      return !empty || !disconnected ? "busy" : "pairing_failed";
    }
    CompletePairingResponse(tokenField, token);
    return nullptr;
  }

  static const char* RecoverLocal(cJSON* response, void* context) {
    auto& bridge = *static_cast<BleStatusBridge*>(context);
    MaintenanceGate::PairingReservation reservation(bridge.maintenance);
    if (!reservation.Acquired()) return "busy";
    const char* error = bridge.PairingPrerequisites(true);
    if (error != nullptr) { return error; }
    auto* tokenField = bridge.PreparePairingResponse(response);
    if (tokenField == nullptr) { return "pairing_failed"; }
    std::array<uint8_t, LocalPairingCredential::TokenSize> token{};
    // The maintenance ticket blocks new interaction while admission/inbox
    // reserve disconnected recovery in RAM. Their callbacks execute unlocked;
    // credential I/O never owns a Stop or control-pass mutex.
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
    bridge.PollAutomaticInstall();
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
    if (std::string(name) == "ota-policy") return bridge.RequestAutomaticInstall(automaticInstall);
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
        if (!runtime_.HasAutonomousPresentation() && !runtime_.HasDecoration()) effects_.Highlight(0, 0, 0, 0);
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
  static TwinObservedIo observedHardware(hardware);
  static PhysicalEffects effects(observedHardware);
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
  // Successful initialization includes an accepted dark LED frame. Failure
  // leaves that driver state unknown rather than inventing a zero frame.
  observedHardware.ledKnown_ = hardwareStarted;
  std::array<ServoCalibration, 6> calibration{};
  uint64_t calibrationRevision = 0;
  const bool calibrated = hardwareStarted && hardware.LoadCalibration(calibration, calibrationRevision) &&
                          effects.Configure(calibration, calibrationRevision);
  if (!calibrated) ESP_LOGW(LogTag, "Hardware wiring/calibration unverified; motion stays disabled");
  static EspIdfJournalStorage journalStorage(nvsStatus == ESP_OK);
  static EspIdfProvisioningStorage identityStorage(nvsStatus == ESP_OK);
  static DeviceIdentity identity(identityStorage);
  static EspIdfJournalIdentityStorage markerStorage(nvsStatus == ESP_OK);
  static EspIdfKeepsakeInventoryStorage onlineStorage(nvsStatus == ESP_OK);
  static kufr::offline::EspIdfOfflineBlobStorage offlineStorage(nvsStatus == ESP_OK);
  static DeviceStorageStartup storageStartup(journalStorage, markerStorage, onlineStorage, offlineStorage,
      {identity.IsHealthy(), identity.IsProvisioned(), identity.DeviceId()},
      DeviceStorageRuntime::Policy::BoundedCompaction);
  static EspIdfDeviceStorageWorker storageWorker(storageStartup);
  const bool storageStarted = storageWorker.Start();
  CommandJournal& journal = storageStartup.ControlJournal();
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
  if (!storageStarted || !storageStartup.ImageStorageHealthy()) {
    safetyController.ReportFault();
    ESP_LOGE(LogTag, "Journal unavailable; control remains faulted (%d)",
             static_cast<int>(nvsStatus));
  } else if (storageStartup.RequiresService()) {
    ESP_LOGW(LogTag, "Journal migration requires explicit USB service; original bytes retained");
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
  presentationPort.BindBootIdentity(bootId);
  static WandPresentation presentation(presentationPort);
  controlRuntime.BindPresentation(presentation);
  controlRuntime.PublishCurrent();
  static ConversationSession conversation(bootId, conversationId);
  static MaintenanceGate maintenance;
  static EspIdfStationNetwork station;
  station.SetChangeGate({MaintenanceGate::AcquireNetwork, MaintenanceGate::ReleaseNetwork, &maintenance});
  const bool stationStarted = station.Start(nvsStatus == ESP_OK);
  static TwinTelemetryMailbox twinMailbox;
  static EspIdfTwinTelemetry twinNetwork(twinMailbox, identity, station, bootId);
  if (stationStarted) twinNetwork.Start();
  alignas(EspIdfBackendNetwork) static std::array<uint8_t, sizeof(EspIdfBackendNetwork)> backendWorkspace{};
  auto* sharedStorage = storageStartup.Runtime();
  auto* backend = sharedStorage
      ? new (backendWorkspace.data()) EspIdfBackendNetwork(identity, runtimeState, bootId, station, *sharedStorage)
      : new (backendWorkspace.data()) EspIdfBackendNetwork(identity, runtimeState, bootId, station, journal, journalStorage);
  auto& backendNetwork = *backend;
  backendNetwork.InitializeVoice(nvsStatus == ESP_OK, hardware.ProfileSelected() ? &hardware : nullptr);
  static TwinObservedAudio observedAudio(backendNetwork.PresentationAudio());
  presentationPort.BindAudio(observedAudio);
  backendNetwork.InitializeCamera(nvsStatus == ESP_OK, hardware.Available());
  if (!stationStarted || !sharedStorage || !backendNetwork.Start(nvsStatus == ESP_OK)) {
    ESP_LOGW(LogTag, "Backend network not configured; stays offline");
  }
  static kufr::ota::EspIdfFirmwareUpdate firmwareUpdate;
  firmwareUpdate.Begin();
  static kufr::assets::EspIdfSdAssets sdAssets;
  sdAssets.Begin();
  static kufr::dance::EspIdfDancePlayer dancePlayer(sdAssets, backendNetwork.DanceAudio());
  dancePlayer.Begin();
  static kufr::dance::DanceCoordinator dance(dancePlayer, effects);
  dance.BindBootIdentity(bootId);
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
  static ShowTraceMailbox showTrace;
  server.BindShowTrace(showTrace);
  static EspIdfLocalServiceAdvertisement advertisement(station, hasher);
  static LocalControlOutput output(
      identity.DeviceId(), bootId, deviceControl, journal, safetyController,
      selectionSequence, conversation, server, &backendNetwork, &dance);
  static RuntimeClock clock;
  presentationPort.BindClock([](void* context) -> uint64_t {
    return static_cast<RuntimeClock*>(context)->NowMs();
  }, &clock);
  static RuntimeLeaseIds leaseIds(random);
  static PhysicalInspection inspection(effects);
  static HardwareSteps steps(effects, controlRuntime);
  static LocalControlOwner owner(
      identity.DeviceId(), bootId, controlRuntime, runtimeState, conversation,
      backendNetwork, inbox, server, output, leaseIds, inspection, steps, clock, &backendNetwork, &dance);
  static kufr::offline::EspIdfOfflineAudio offlineAudio(backendNetwork.DanceAudio());
  offlineAudio.Begin();
  alignas(kufr::offline::OfflineEvidenceBridge) static std::array<uint8_t, sizeof(kufr::offline::OfflineEvidenceBridge)> bridgeWorkspace{};
  auto* offlineBridge = sharedStorage ? new (bridgeWorkspace.data()) kufr::offline::OfflineEvidenceBridge(
      sharedStorage->EvidenceRequests(), *sharedStorage) : nullptr;
  static UnavailableOfflineStorage unavailableOffline;
  alignas(kufr::offline::OfflineShow) static std::array<uint8_t, sizeof(kufr::offline::OfflineShow)> offlineWorkspace{};
  auto* offlineProducer = offlineBridge
      ? new (offlineWorkspace.data()) kufr::offline::OfflineShow(controlRuntime, runtimeState, deviceControl,
          presentation, presentationPort, offlineAudio, *offlineBridge, identity.DeviceId(), bootId, bootId)
      : new (offlineWorkspace.data()) kufr::offline::OfflineShow(controlRuntime, runtimeState, deviceControl,
          presentation, presentationPort, offlineAudio, unavailableOffline, identity.DeviceId(), bootId, bootId);
  auto& offline = *offlineProducer;
  owner.BindOffline(offline);
  static DeviceShowRuntime deviceShow(controlRuntime, runtimeState, presentationPort,
      offline, offlineAudio, backendNetwork.ShowCues(), bootId);
  bool serverStarted = false;
  uint64_t retryServerAtMs = 0;
  bool advertisementStarted = false;
  uint64_t retryAdvertisementAtMs = 0;
  bool rawButton = false, button = false, gestureConsumed = false;
  bool hardwareFaultHandled = false;
  bool hardwareWasInspected = effects.Healthy();
  uint8_t pendingButtonAction = 0; // 1: prepared offline scene; 2: explicit online voice.
  uint64_t buttonChangedAt = 0, buttonPressedAt = 0;
  bool lastTraceWasDance = false;
  static DeviceControlRuntime::RecoveryHandle physicalRecovery;
  PresentationIdentity<97> observedPresentationId;
  uint32_t observedPresentationSequence = 0;
  ESP_LOGI(LogTag, "ESP-IDF control loop started; physical outputs require verified service configuration");
  static_assert(CONFIG_ESP_MAIN_TASK_STACK_SIZE >= 16384,
                "TLS identity startup requires at least 16 KiB of main task stack");
  ESP_LOGI(LogTag, "Main task stack: configured %u bytes, minimum remaining %u bytes",
           static_cast<unsigned>(CONFIG_ESP_MAIN_TASK_STACK_SIZE),
           static_cast<unsigned>(uxTaskGetStackHighWaterMark(nullptr)));
  // Control preempts the storage/network owners; no source I/O runs here.
  vTaskPrioritySet(nullptr, 6);

  while (true) {
    ControlMaintenanceGate::Ticket controlPass;
    const bool ownsControlPass = maintenance.ownership.BeginControl(controlPass);
    const auto maintenanceOwner = maintenance.ownership.HeldBy();
    const bool otaHeld = maintenanceOwner == ControlMaintenanceGate::Owner::Ota;
    const uint64_t nowMs = clock.NowMs();
    TurnProposal pendingProposal;
    const bool safeIdle = !deviceControl.HasManualControl() && !deviceControl.HasPendingSelection() &&
        !controlRuntime.HasStorageWork() && !storageStartup.HasWork() && !selectionSequence.IsBusy() &&
        !controlRuntime.HasAutonomousPresentation() && !controlRuntime.HasDecoration() && !conversation.HasActiveTurn() &&
        !backendNetwork.IsVoiceBusy() && !dance.Reserved() && !offline.HasPendingStorage() && !offline.Active() &&
        !backendNetwork.ReadPendingProposal(pendingProposal, nowMs);
    // The whole-cycle deadline precedes physical output processing, even when
    // per-phase calibration timeouts would still allow the sequence to run.
    presentation.CheckDeadline(nowMs);
    effects.Tick(nowMs);
    const bool selfTestReady = kufr::ota::BootSelfTestReady(
        nvsStatus == ESP_OK && storageStarted && storageStartup.ImageStorageHealthy() && stationStarted &&
            (!kufr::offline::CompiledBank().complete || offlineAudio.Available()),
        hardware.ProfileSelected(), calibrated, effects);
    firmwareUpdate.Tick(nowMs, station.HasIp(), selfTestReady,
        safeIdle && (ownsControlPass || otaHeld) && !sdAssets.Reserved());
    sdAssets.Tick(safeIdle && (ownsControlPass || otaHeld) && !firmwareUpdate.Reserved());
    const auto otaStatus = firmwareUpdate.Status();
    // Boot verification freezes new interactions while permitting STA recovery.
    const bool needsOtaReservation = sdAssets.Reserved() ||
        (firmwareUpdate.Reserved() && otaStatus.state != "verifying-boot");
    const bool maintaining = !storageStarted || !sharedStorage || !sharedStorage->AllowsAdmission() ||
        controlRuntime.HasRecoveryWork() || !ownsControlPass ||
        firmwareUpdate.Reserved() || sdAssets.Reserved();
    const bool recoveryAdmission = ownsControlPass && storageStarted && !storageStartup.RequiresService() &&
        !firmwareUpdate.Reserved() && !sdAssets.Reserved();
    owner.SetMaintenance(maintaining);
    owner.SetRecoveryAdmission(recoveryAdmission);
    owner.SetDanceAdmission(safeIdle && !maintaining);
    provisioning.Tick(nowMs, station.HasIp());
    if (!serverStarted && ownsControlPass &&
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
    if (effects.Healthy()) { hardwareFaultHandled = false; hardwareWasInspected = true; }
    if (calibrated && !effects.Healthy() && !hardwareFaultHandled) {
      hardwareFaultHandled = true;
      SelectionStep stopped;
      dance.StopDance();
      // Initial boot inspection is still required; it does not revoke a saved
      // offline opt-in. A fault after inspection does revoke it like Stop.
      if (hardwareWasInspected) offline.Stop();
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
        const bool ambientWasActive = offline.AmbientActive();
        if (ambientWasActive) offline.PrepareInteraction();
        if (!ambientWasActive && (offline.Active() || controlRuntime.HasStorageWork() ||
            controlRuntime.HasAutonomousPresentation() || controlRuntime.HasDecoration() || selectionSequence.IsBusy() || dance.Reserved())) {
          pendingButtonAction = 0;
          SelectionStep stopped;
          dance.StopDance();
          offline.Stop();
          controlRuntime.Stop(stopped);
          backendNetwork.StopVoice();
          steps.OnStep(stopped);
          gestureConsumed = true;
        }
      } else if (!gestureConsumed && !maintaining) {
        pendingButtonAction = offline.Armed() ? 1 : controlRuntime.OfflineAuthority() ? 0 : 2;
      }
    }
    // Cancellation is immediate; a newly accepted audio owner waits for the
    // bounded previous player to quiesce so no old completion can mute it.
    if (pendingButtonAction && !maintaining && offline.PrepareInteraction()) {
      const uint8_t action = pendingButtonAction;
      pendingButtonAction = 0;
      if (action == 1) offline.StartNext(nowMs);
      else {
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
    if (recoveryAdmission && button && !gestureConsumed && nowMs - buttonPressedAt >= 2000) {
      gestureConsumed = true;
      if (!selectionSequence.IsBusy() && !controlRuntime.HasAutonomousPresentation() && !controlRuntime.HasDecoration() && !dance.Reserved() &&
          effects.RecoverAfterInspection()) controlRuntime.BeginInspectedRecovery(physicalRecovery);
    }
    if (physicalRecovery.IsBound()) {
      const auto progress = controlRuntime.PollInspectedRecovery(physicalRecovery);
      if (progress != DeviceControlRuntime::RecoveryProgress::Pending)
        controlRuntime.FinishInspectedRecovery(physicalRecovery);
    }
    server.Pump();
    owner.Tick();
    inbox.RunIfNoUrgent([&]() {
      const uint64_t effectNowMs = clock.NowMs();
      dance.Tick(effectNowMs);
      controlRuntime.SetExternalActivity(dance.Reserved());
      presentation.CheckDeadline(effectNowMs);
      presentationPort.Tick(effectNowMs);
      offline.SetVoice(backendNetwork.VoiceStatus().voice);
      deviceShow.SetVoice(backendNetwork.VoiceStatus().voice);
      deviceShow.Tick(effectNowMs, !maintaining && !dance.Reserved() &&
          !deviceControl.HasManualControl(),
          !conversation.HasActiveTurn() && !backendNetwork.IsConversationVoiceBusy());
      if (deviceShow.TakeRemoteStop()) {
        dance.StopDance();
        backendNetwork.StopVoice();
      }
      offline.Tick(effectNowMs, !maintaining && !dance.Reserved() &&
          !deviceControl.HasManualControl() && !conversation.HasActiveTurn() &&
          !backendNetwork.IsVoiceBusy() && !deviceShow.Active() &&
          (!backendNetwork.IsOnline() || controlRuntime.OfflineAuthority()));
    });
    owner.PumpReply(backendNetwork);
    // Owner has applied urgent Stop/lease changes before any autonomous start.
    inbox.RunIfNoUrgent([&]() {
      if (!controlRuntime.OfflineAuthority() && !maintaining && !dance.Reserved() && !deviceControl.HasManualControl() && effects.Healthy())
        backendNetwork.BeginPendingPresentation(presentation, clock.NowMs());
    });
    backendNetwork.FinishPresentation(presentation);
    const auto& presentationTrace = presentationPort.Diagnostics();
    if (std::strcmp(observedPresentationId.data(), presentationTrace.operationId.data()) != 0 ||
        observedPresentationSequence != presentationTrace.lastSequence) {
      observedPresentationId = presentationTrace.operationId;
      observedPresentationSequence = presentationTrace.lastSequence;
      lastTraceWasDance = false;
    }
    if (dance.Reserved()) lastTraceWasDance = true;
    showTrace.Publish(lastTraceWasDance ? dance.Diagnostics() : presentationTrace, clock.NowMs());
    static TwinTelemetrySnapshot twinSnapshot;
    static uint64_t twinSequence = 0, twinPublishedAt = 0;
    const uint64_t twinNow = clock.NowMs();
    if (twinNow - twinPublishedAt >= 500) {
      twinPublishedAt = twinNow;
      twinSnapshot.trace = lastTraceWasDance ? dance.Diagnostics() : presentationTrace;
      twinSnapshot.sequence = ++twinSequence;
      twinSnapshot.timeMs = twinNow;
      twinSnapshot.revision = deviceControl.Revision();
      twinSnapshot.generation = controlRuntime.Generation();
      twinSnapshot.calibration = effects.CalibrationRevision();
      twinSnapshot.recovery = selectionSequence.NeedsRecovery() || !effects.Healthy() ||
          (!controlRuntime.HasAutonomousPresentation() && deviceControl.NeedsRecovery());
      twinSnapshot.readyMask = deviceControl.ReadyMask();
      twinSnapshot.slot = presentation.Compartment();
      switch (presentation.State()) {
        case WandPresentationState::Idle:
          twinSnapshot.state = dance.Reserved() ? "dance" : selectionSequence.IsBusy() ? "manual-selection" : "idle";
          twinSnapshot.slot = selectionSequence.IsBusy() ? selectionSequence.SelectedCompartment() : 0;
          break;
        case WandPresentationState::Raising: twinSnapshot.state = "raising"; break;
        case WandPresentationState::Showing: twinSnapshot.state = "showing"; break;
        case WandPresentationState::Lowering: twinSnapshot.state = "lowering"; break;
        case WandPresentationState::Completing: twinSnapshot.state = "lowering"; break;
        case WandPresentationState::AwaitingAdmission: twinSnapshot.state = "idle"; break;
        case WandPresentationState::Fault: twinSnapshot.state = "fault"; break;
      }
      twinSnapshot.pulse = observedHardware.pulse_;
      twinSnapshot.led = observedHardware.led_;
      twinSnapshot.ledKnown = observedHardware.ledKnown_;
      twinSnapshot.enabled = observedHardware.enabled_;
      twinSnapshot.vibrationMask = observedHardware.vibrationMask_;
      twinSnapshot.audio = backendNetwork.IsVoiceBusy() ? "voice-busy-reported" : observedAudio.Status();
      for (uint8_t slot = 1; slot <= 6; ++slot) {
        twinSnapshot.estimate[slot - 1] = effects.IsResting(slot) ? 1 : effects.IsRaised(slot) ? 2 :
            effects.Healthy() && observedHardware.enabled_ ? 3 : 0;
      }
      twinMailbox.Publish(twinSnapshot);
    }
    // Publish idle only after all admissions in this iteration have completed.
    // An old pending journal record still requires inspection; it is not an
    // active actuator and must not prevent offline-safe network/OTA recovery.
    const bool completedSafeIdle = !deviceControl.HasManualControl() && !deviceControl.HasPendingSelection() &&
        !controlRuntime.HasStorageWork() && !storageStartup.HasWork() && !selectionSequence.IsBusy() &&
        !controlRuntime.HasAutonomousPresentation() && !controlRuntime.HasDecoration() && !conversation.HasActiveTurn() &&
        !backendNetwork.IsVoiceBusy() && !dance.Reserved() && !offline.HasPendingStorage() && !offline.Active() &&
        !backendNetwork.ReadPendingProposal(pendingProposal, clock.NowMs());
    if (ownsControlPass) {
      if (needsOtaReservation) {
        // No idle publication window may admit STA/pairing between enqueueing
        // an exclusive OTA/SD operation and transferring its reservation.
        maintenance.ownership.TransferControl(controlPass, ControlMaintenanceGate::Owner::Ota, maintenance.ota);
      } else {
        maintenance.ownership.EndControl(controlPass, completedSafeIdle);
      }
    } else if (otaHeld && !needsOtaReservation) {
      maintenance.ownership.Release(ControlMaintenanceGate::Owner::Ota, maintenance.ota);
    }
    vTaskDelay(pdMS_TO_TICKS(ControlLoopPeriodMs));
  }
}

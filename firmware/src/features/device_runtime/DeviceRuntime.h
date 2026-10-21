#pragma once

#include "../device_control/DeviceControlRuntime.h"
#include "../device_control/DeviceStorageStartup.h"
#include "../show_diagnostics/ShowTraceMailbox.h"
#include "../twin_telemetry/TwinTelemetry.h"
#include "../backend_access/EspIdfBackendNetwork.h"
#include "../backend_access/EspIdfStationNetwork.h"
#include "../conversation/EspIdfConversationUuid.h"
#include "../local_control/LocalControlOwner.h"
#include "../physical_effects/EspIdfPhysicalIo.h"
#include "../physical_effects/PhysicalPresentation.h"
#include "../dance/DanceCoordinator.h"
#include "../network_provisioning/EspIdfBleProvisioning.h"
#include "../firmware_update/EspIdfFirmwareUpdate.h"
#include "../sd_assets/EspIdfSdAssets.h"
#include "../offline_show/EspIdfOfflineShow.h"
#include "../show_cues/DeviceShowRuntime.h"
#include "MaintenanceReservations.h"
#include "ControlSchedule.h"
#include "RuntimeDiagnostics.h"

namespace kufr { namespace runtime {

struct DeviceRuntimePorts final {
  EspIdfPhysicalIo& hardware;
  TwinObservedIo& observedHardware;
  PhysicalEffects& effects;
  SelectionSequence& selectionSequence;
  DeviceControl& deviceControl;
  DeviceControlRuntime& controlRuntime;
  PhysicalPresentation& presentationPort;
  WandPresentation& presentation;
  ConversationSession& conversation;
  ConversationRuntimeState& runtimeState;
  MaintenanceReservations& maintenance;
  EspIdfStationNetwork& station;
  EspIdfBackendNetwork& backendNetwork;
  kufr::ota::EspIdfFirmwareUpdate& firmwareUpdate;
  kufr::assets::EspIdfSdAssets& sdAssets;
  kufr::dance::DanceCoordinator& dance;
  LocalControlInbox& inbox;
  EspIdfBleProvisioning& provisioning;
  LocalControlOwner& owner;
  kufr::offline::OfflineShow& offline;
  DeviceShowRuntime& deviceShow;
  ShowTraceMailbox& showTrace;
  LocalControlClockPort& clock;
  LocalControlStepPort& steps;
  TwinTelemetryMailbox& twinMailbox;
  TwinObservedAudio& observedAudio;
  DeviceStorageStartup& storageStartup;
  kufr::offline::EspIdfOfflineAudio& offlineAudio;
  DeviceStorageRuntime* sharedStorage;
  bool nvsReady;
  bool storageStarted;
  bool stationStarted;
  bool calibrated;
};

class DeviceRuntime final {
 public:
  explicit DeviceRuntime(DeviceRuntimePorts ports) : ports_(ports), hardwareWasInspected(ports.effects.Healthy()) {}
  void Run();
  void Step();
  const RuntimeDiagnostics& Diagnostics() const { return diagnostics_; }
 private:
  struct ControlPass final {
    ControlMaintenanceGate::Ticket controlPass;
    uint64_t nowMs = 0;
    bool ownsControlPass = false, otaHeld = false, safeIdle = false;
    bool needsOtaReservation = false, maintaining = true, recoveryAdmission = false;
  };
  bool SafeIdle(uint64_t nowMs);
  void ConfigureAdmission(ControlPass& pass);
  void ProcessSafety(ControlPass& pass);
  void Maintain(ControlPass& pass);
  void AdvanceShows(ControlPass& pass);
  void PublishTelemetry();
  void EndPass(ControlPass& pass);
  ActuatorOwner CurrentOwner(const ControlPass& pass) const;
  DeviceRuntimePorts ports_;
  RuntimeDiagnostics diagnostics_;
  RuntimeSnapshot snapshot_;
  bool rawButton = false, button = false, gestureConsumed = false;
  bool hardwareFaultHandled = false;
  bool hardwareWasInspected;
  uint8_t pendingButtonAction = 0; // 1: prepared offline scene; 2: explicit online voice.
  uint64_t buttonChangedAt = 0, buttonPressedAt = 0;
  bool lastTraceWasDance = false;
  DeviceControlRuntime::RecoveryHandle physicalRecovery;
  PresentationIdentity<97> observedPresentationId;
  uint32_t observedPresentationSequence = 0;
  TwinTelemetrySnapshot twinSnapshot;
  uint64_t twinSequence = 0, twinPublishedAt = 0;
};

} }

#pragma once

#include <atomic>
#include "MaintenanceReservations.h"
#include "DeviceDiagnostics.h"
#include "../device_control/EspIdfDeviceStorageWorker.h"
#include "../physical_effects/EspIdfPhysicalIo.h"
#include "../firmware_update/EspIdfFirmwareUpdate.h"
#include "../backend_access/EspIdfBackendNetwork.h"
#include "../local_control/EspIdfLocalControlServer.h"
#include "../local_control/EspIdfLocalServiceAdvertisement.h"
#include "../backend_access/EspIdfStationNetwork.h"
#include "DeviceIdentity.h"

namespace kufr { namespace runtime {

// Owns service startup/retries and HTTP work scheduling. Never calls a domain
// control method. A logical safe-idle reservation freezes admission while TLS
// allocates, while the control task continues Stop, fault and lease handling.
class EspIdfControlServices final : public RuntimeDiagnosticsPort {
 public:
  EspIdfControlServices(EspIdfLocalControlServer& server,
      EspIdfLocalServiceAdvertisement& advertisement, EspIdfStationNetwork& station,
      DeviceIdentity& identity, LocalTlsIdentity& tls, LocalPairingCredential& pairing,
      MaintenanceReservations& maintenance)
      : server_(server), advertisement_(advertisement), station_(station),
        identity_(identity), tls_(tls), pairing_(pairing), maintenance_(maintenance) {}
  bool Start();
  bool ServerStarted() const { return serverStarted_.load(); }
  void BindDiagnostics(const RuntimeDiagnostics& runtime, DeviceStorageStartup& storage,
      EspIdfDeviceStorageWorker& storageWorker, EspIdfPhysicalIo& hardware,
      kufr::ota::EspIdfFirmwareUpdate& ota, EspIdfBackendNetwork& backend,
      bool calibrationConfigured, TaskHandle_t controlTask);
  bool ReadDiagnostics(DeviceDiagnosticSnapshot& snapshot) const override;
 private:
  static void Task(void* context);
  void Run();
  void SampleDiagnostics(uint64_t nowMs);
  EspIdfLocalControlServer& server_;
  EspIdfLocalServiceAdvertisement& advertisement_;
  EspIdfStationNetwork& station_;
  DeviceIdentity& identity_;
  LocalTlsIdentity& tls_;
  LocalPairingCredential& pairing_;
  MaintenanceReservations& maintenance_;
  std::atomic<bool> serverStarted_{false};
  TaskHandle_t task_ = nullptr;
  const RuntimeDiagnostics* runtime_ = nullptr;
  DeviceStorageStartup* storage_ = nullptr;
  EspIdfDeviceStorageWorker* storageWorker_ = nullptr;
  EspIdfPhysicalIo* hardware_ = nullptr;
  kufr::ota::EspIdfFirmwareUpdate* ota_ = nullptr;
  EspIdfBackendNetwork* backend_ = nullptr;
  bool calibrationConfigured_ = false;
  TaskHandle_t controlTask_ = nullptr;
  DeviceHealthMailbox health_;
  // Snapshot scratch belongs to this worker, never the actuator task stack.
  JournalSnapshot journal_;
};

} }

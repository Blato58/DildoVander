#include "EspIdfControlServices.h"

#include "esp_timer.h"
#include "esp_heap_caps.h"
#include "esp_system.h"
#include <cstring>
#include "../provisioning/LocalTlsIdentity.h"

namespace kufr { namespace runtime {

bool EspIdfControlServices::Start() {
  return task_ || xTaskCreate(Task, "control_services", 8192, this, 3, &task_) == pdPASS;
}

void EspIdfControlServices::Task(void* context) {
  static_cast<EspIdfControlServices*>(context)->Run();
}

void EspIdfControlServices::Run() {
  uint64_t retryAtMs = 0, sampleAtMs = 0;
  bool advertisementStarted = false;
  for (;;) {
    const uint64_t nowMs = static_cast<uint64_t>(esp_timer_get_time() / 1000);
    if ((!serverStarted_.load() || !advertisementStarted) && nowMs >= retryAtMs && station_.HasIp()) {
      MaintenanceReservations::PairingReservation reservation(maintenance_);
      if (reservation.Acquired()) {
        retryAtMs = nowMs + 5000;
        if (!serverStarted_.load() && identity_.IsHealthy() && identity_.IsProvisioned() &&
            tls_.IsHealthy() && tls_.IsProvisioned() && pairing_.IsHealthy()) {
          serverStarted_.store(server_.Start());
        }
        if (serverStarted_.load() && !advertisementStarted) {
          advertisementStarted = advertisement_.Start(identity_.DeviceId());
        }
      }
    }
    if (serverStarted_.load()) server_.Pump();
    if (nowMs >= sampleAtMs) { SampleDiagnostics(nowMs); sampleAtMs = nowMs + 1000; }
    vTaskDelay(pdMS_TO_TICKS(20));
  }
}

void EspIdfControlServices::BindDiagnostics(const RuntimeDiagnostics& runtime,
    DeviceStorageStartup& storage, EspIdfDeviceStorageWorker& storageWorker,
    EspIdfPhysicalIo& hardware, kufr::ota::EspIdfFirmwareUpdate& ota,
    EspIdfBackendNetwork& backend, bool calibrationConfigured, TaskHandle_t controlTask) {
  // Boot composition only, before this worker starts.
  runtime_ = &runtime;
  storage_ = &storage;
  storageWorker_ = &storageWorker;
  hardware_ = &hardware;
  ota_ = &ota;
  backend_ = &backend;
  calibrationConfigured_ = calibrationConfigured;
  controlTask_ = controlTask;
}

bool EspIdfControlServices::ReadDiagnostics(DeviceDiagnosticSnapshot& snapshot) const {
  if (!runtime_ || !runtime_->Read(snapshot.runtime) || !health_.Read(snapshot.health)) return false;
  // This urgent gate is an atomic RAM fact; do not delay it until the next
  // one-second heap/status sample or represent it as a completed domain Stop.
  snapshot.health[DeviceHealth::UrgentStopPending] = hardware_->UrgentStopPending();
  return true;
}

void EspIdfControlServices::SampleDiagnostics(uint64_t nowMs) {
  if (!runtime_) return;
  DeviceHealth sample;
  const auto setWide = [&](DeviceHealth::Field low, DeviceHealth::Field high, uint64_t value) {
    sample[low] = static_cast<uint32_t>(value);
    sample[high] = static_cast<uint32_t>(value >> 32);
  };
  setWide(DeviceHealth::SampledAtLow, DeviceHealth::SampledAtHigh, nowMs);
  sample[DeviceHealth::ResetReason] = static_cast<uint32_t>(esp_reset_reason());
  sample[DeviceHealth::Profile] = hardware_->ProfileSelected() ? 2 : 0;
  sample[DeviceHealth::CalibrationConfigured] = calibrationConfigured_;
  sample[DeviceHealth::HardwareAvailable] = hardware_->Available();
  sample[DeviceHealth::HasIp] = station_.HasIp();
  sample[DeviceHealth::StationChanging] = station_.IsChangingNetwork();
  const auto station = station_.Status();
  sample[DeviceHealth::ActiveProfile] = static_cast<uint32_t>(station.active);
  sample[DeviceHealth::Station] = static_cast<uint32_t>(
      std::strcmp(station.phase, "offline") == 0 ? StationPhase::Offline :
      std::strcmp(station.phase, "connecting") == 0 ? StationPhase::Connecting :
      std::strcmp(station.phase, "connected") == 0 ? StationPhase::Connected : StationPhase::Unknown);
  sample[DeviceHealth::BackendOnline] = backend_->IsOnline();
  sample[DeviceHealth::Authentication] = backend_->AuthenticationState();
  sample[DeviceHealth::TrustedTime] = backend_->TrustedTimeState();
  VoiceMediaHealth media;
  if (backend_->ReadVoiceMediaHealth(media) && media.session != 0) {
    sample[DeviceHealth::MediaAvailable] = true;
    setWide(DeviceHealth::MediaSessionLow, DeviceHealth::MediaSessionHigh, media.session);
    setWide(DeviceHealth::MediaCancellationLow, DeviceHealth::MediaCancellationHigh, media.cancellationEpoch);
    setWide(DeviceHealth::MediaPlayedLow, DeviceHealth::MediaPlayedHigh, media.playedPcmBytes);
    sample[DeviceHealth::CaptureQueuedBytes] = media.captureQueuedBytes;
    sample[DeviceHealth::CaptureHighWaterBytes] = media.captureHighWaterBytes;
    sample[DeviceHealth::CaptureOverruns] = media.captureOverruns;
    sample[DeviceHealth::PlaybackQueuedBytes] = media.playbackQueuedBytes;
    sample[DeviceHealth::PlaybackHighWaterBytes] = media.playbackHighWaterBytes;
    sample[DeviceHealth::MediaTermination] = media.termination;
  }
  const auto ota = ota_->Status();
  OtaPhase phase = OtaPhase::Unknown;
  const char* const states[] = {"unknown", "disabled", "idle", "verifying-boot", "checking", "available", "current",
      "waiting-idle", "downloading", "verifying-image", "ready-to-reboot", "rebooting", "rollback", "finalizing-boot",
      "service-required", "error"};
  for (size_t index = 0; index < sizeof(states) / sizeof(states[0]); ++index)
    if (ota.state == states[index]) phase = static_cast<OtaPhase>(index);
  sample[DeviceHealth::Ota] = static_cast<uint32_t>(phase);
  sample[DeviceHealth::StorageStartup] = static_cast<uint32_t>(storage_->CurrentState());
  sample[DeviceHealth::StorageState] = 255;
  if (auto* runtime = storage_->Runtime()) {
    sample[DeviceHealth::StorageState] = static_cast<uint32_t>(runtime->CurrentState());
    setWide(DeviceHealth::CompactionsLow, DeviceHealth::CompactionsHigh, runtime->Compactions());
    sample[DeviceHealth::StorageBusy] = storage_->HasWork();
    if (runtime->ReadJournalSnapshot(journal_)) {
      sample[DeviceHealth::JournalKnown] = true;
      sample[DeviceHealth::JournalHealthy] = journal_.healthy;
      sample[DeviceHealth::JournalUsed] = static_cast<uint32_t>(journal_.historyCount);
      setWide(DeviceHealth::JournalEpochLow, DeviceHealth::JournalEpochHigh, journal_.currentEpoch);
    }
  }
  constexpr uint32_t internal = MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT;
  constexpr uint32_t psram = MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT;
  sample[DeviceHealth::InternalFree] = static_cast<uint32_t>(heap_caps_get_free_size(internal));
  sample[DeviceHealth::InternalMinimum] = static_cast<uint32_t>(heap_caps_get_minimum_free_size(internal));
  sample[DeviceHealth::InternalLargest] = static_cast<uint32_t>(heap_caps_get_largest_free_block(internal));
  sample[DeviceHealth::PsramFree] = static_cast<uint32_t>(heap_caps_get_free_size(psram));
  sample[DeviceHealth::PsramMinimum] = static_cast<uint32_t>(heap_caps_get_minimum_free_size(psram));
  sample[DeviceHealth::PsramLargest] = static_cast<uint32_t>(heap_caps_get_largest_free_block(psram));
  // ESP-IDF FreeRTOS reports high-water marks in bytes on this pinned target.
  sample[DeviceHealth::ControlStackMinimum] = static_cast<uint32_t>(uxTaskGetStackHighWaterMark(controlTask_));
  sample[DeviceHealth::ServiceStackMinimum] = static_cast<uint32_t>(uxTaskGetStackHighWaterMark(nullptr));
  sample[DeviceHealth::StorageStackMinimum] = storageWorker_->MinimumStackFreeBytes();
  sample[DeviceHealth::StorageMaximumOperationUs] = storageWorker_->MaximumOperationMicros();
  sample[DeviceHealth::UrgentStopPending] = hardware_->UrgentStopPending();
  health_.Publish(sample);
}

} }

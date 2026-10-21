#include "DeviceDiagnostics.h"

namespace kufr { namespace runtime {
namespace {
const char* StationName(uint32_t phase) {
  switch (static_cast<StationPhase>(phase)) {
    case StationPhase::Offline: return "offline";
    case StationPhase::Connecting: return "connecting";
    case StationPhase::Connected: return "connected";
    default: return "unknown";
  }
}
const char* OtaName(uint32_t phase) {
  switch (static_cast<OtaPhase>(phase)) {
    case OtaPhase::Disabled: return "disabled";
    case OtaPhase::Idle: return "idle";
    case OtaPhase::VerifyingBoot: return "verifying-boot";
    case OtaPhase::Checking: return "checking";
    case OtaPhase::Available: return "available";
    case OtaPhase::Current: return "current";
    case OtaPhase::WaitingIdle: return "waiting-idle";
    case OtaPhase::Downloading: return "downloading";
    case OtaPhase::VerifyingImage: return "verifying-image";
    case OtaPhase::ReadyToReboot: return "ready-to-reboot";
    case OtaPhase::Rebooting: return "rebooting";
    case OtaPhase::Rollback: return "rollback";
    case OtaPhase::FinalizingBoot: return "finalizing-boot";
    case OtaPhase::ServiceRequired: return "service-required";
    case OtaPhase::Error: return "error";
    default: return "unknown";
  }
}
bool ValidBuild(const char* value) {
  if (!value) return false;
  size_t length = 0;
  for (; value[length] && length < 65; ++length) {
    const char c = value[length];
    if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
          (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '_')) return false;
  }
  return length && length <= 64;
}
uint64_t Wide(const DeviceHealth& health, DeviceHealth::Field low, DeviceHealth::Field high) {
  return health[low] | (static_cast<uint64_t>(health[high]) << 32);
}
const char* Boolean(uint32_t value) { return value ? "true" : "false"; }
const char* AuthenticationName(uint32_t value) {
  const char* const states[] = {"unknown", "authorized", "expired", "revoked", "failed"};
  return value < 5 ? states[value] : states[0];
}
const char* TimeName(uint32_t value) {
  const char* const states[] = {"unknown", "waiting", "trusted", "rejected"};
  return value < 4 ? states[value] : states[0];
}
}

std::string RuntimeDiagnosticsPort::Json() const {
  DeviceDiagnosticSnapshot snapshot;
  if (!ReadDiagnostics(snapshot)) return "";
#ifdef KUFR_FIRMWARE_BUILD_ID
  return Serialize(snapshot, KUFR_FIRMWARE_BUILD_ID);
#else
  return Serialize(snapshot, "unknown");
#endif
}

std::string RuntimeDiagnosticsPort::Serialize(const DeviceDiagnosticSnapshot& snapshot, const char* buildId) {
  if (!ValidBuild(buildId)) return "";
  const auto& h = snapshot.health;
  const auto& r = snapshot.runtime;
  const auto& t = r.timing;
  const auto number = [](uint64_t value) { return std::to_string(value); };
  std::string json = "{\"version\":1,\"sampledAtMs\":" + number(Wide(h, DeviceHealth::SampledAtLow, DeviceHealth::SampledAtHigh)) +
      ",\"buildId\":\"" + buildId + "\",\"resetReason\":" + number(h[DeviceHealth::ResetReason]) +
      ",\"profile\":" + number(h[DeviceHealth::Profile]) +
      ",\"calibrationConfigured\":" + Boolean(h[DeviceHealth::CalibrationConfigured]) +
      ",\"hardwareAvailable\":" + Boolean(h[DeviceHealth::HardwareAvailable]) +
      ",\"station\":{\"phase\":\"" + StationName(h[DeviceHealth::Station]) +
      "\",\"activeProfile\":" + number(h[DeviceHealth::ActiveProfile]) +
      ",\"hasIp\":" + Boolean(h[DeviceHealth::HasIp]) + ",\"changing\":" + Boolean(h[DeviceHealth::StationChanging]) +
      "},\"backend\":{\"online\":" + Boolean(h[DeviceHealth::BackendOnline]) +
      ",\"authentication\":\"" + AuthenticationName(h[DeviceHealth::Authentication]) +
      "\",\"time\":\"" + TimeName(h[DeviceHealth::TrustedTime]) + "\"},\"ota\":\"" + OtaName(h[DeviceHealth::Ota]) +
      "\",\"storage\":{\"startup\":" + number(h[DeviceHealth::StorageStartup]) +
      ",\"state\":" + number(h[DeviceHealth::StorageState]) +
      ",\"journalKnown\":" + Boolean(h[DeviceHealth::JournalKnown]) +
      ",\"journalHealthy\":" + Boolean(h[DeviceHealth::JournalHealthy]) +
      ",\"journalUsed\":" + number(h[DeviceHealth::JournalUsed]) + ",\"journalCapacity\":32" +
      ",\"operationEpoch\":\"" + number(Wide(h, DeviceHealth::JournalEpochLow, DeviceHealth::JournalEpochHigh)) +
      "\",\"compactions\":\"" + number(Wide(h, DeviceHealth::CompactionsLow, DeviceHealth::CompactionsHigh)) +
      "\",\"busy\":" + Boolean(h[DeviceHealth::StorageBusy]) +
      "},\"memory\":{\"internalFree\":" + number(h[DeviceHealth::InternalFree]) +
      ",\"internalMinimum\":" + number(h[DeviceHealth::InternalMinimum]) +
      ",\"internalLargest\":" + number(h[DeviceHealth::InternalLargest]) +
      ",\"psramFree\":" + number(h[DeviceHealth::PsramFree]) +
      ",\"psramMinimum\":" + number(h[DeviceHealth::PsramMinimum]) +
      ",\"psramLargest\":" + number(h[DeviceHealth::PsramLargest]) +
      "},\"stack\":{\"controlMinimum\":" + number(h[DeviceHealth::ControlStackMinimum]) +
      ",\"serviceMinimum\":" + number(h[DeviceHealth::ServiceStackMinimum]) +
      ",\"storageMinimum\":" + number(h[DeviceHealth::StorageStackMinimum]) +
      "},\"control\":{\"passes\":" + number(t.passes) + ",\"skipped\":" + number(t.skippedReleases) +
      ",\"executionUs\":" + number(t.executionUs) + ",\"maxExecutionUs\":" + number(t.maximumExecutionUs) +
      ",\"latenessUs\":" + number(t.latenessUs) + ",\"maxLatenessUs\":" + number(t.maximumLatenessUs) +
      ",\"gapUs\":" + number(t.gapUs) + ",\"maxGapUs\":" + number(t.maximumGapUs) +
      ",\"owner\":" + number(static_cast<uint32_t>(r.owner)) + ",\"maintenanceOwner\":" + number(r.maintenanceOwner) +
      "},\"physical\":{\"maxTransferUs\":" + number(r.maximumIoTransferUs) +
      ",\"maxPassBusUs\":" + number(r.maximumPassBusUs) + ",\"lockRefusals\":" + number(r.ioLockRefusals) +
      ",\"budgetRefusals\":" + number(r.ioBudgetRefusals) + ",\"coalescedLedFrames\":" + number(r.coalescedLedFrames) +
      ",\"overBudgetPasses\":" + number(r.overBudgetPasses) +
      ",\"urgentStopPending\":" + Boolean(h[DeviceHealth::UrgentStopPending]) +
      "},\"storageMaxOperationUs\":" + number(h[DeviceHealth::StorageMaximumOperationUs]) +
      ",\"mediaAvailable\":" + Boolean(h[DeviceHealth::MediaAvailable]) +
      ",\"media\":{\"session\":\"" + number(Wide(h, DeviceHealth::MediaSessionLow, DeviceHealth::MediaSessionHigh)) +
      "\",\"cancellationEpoch\":\"" + number(Wide(h, DeviceHealth::MediaCancellationLow, DeviceHealth::MediaCancellationHigh)) +
      "\",\"playedPcmBytes\":\"" + number(Wide(h, DeviceHealth::MediaPlayedLow, DeviceHealth::MediaPlayedHigh)) +
      "\",\"captureQueuedBytes\":" + number(h[DeviceHealth::CaptureQueuedBytes]) +
      ",\"captureHighWaterBytes\":" + number(h[DeviceHealth::CaptureHighWaterBytes]) +
      ",\"captureOverruns\":" + number(h[DeviceHealth::CaptureOverruns]) +
      ",\"playbackQueuedBytes\":" + number(h[DeviceHealth::PlaybackQueuedBytes]) +
      ",\"playbackHighWaterBytes\":" + number(h[DeviceHealth::PlaybackHighWaterBytes]) +
      ",\"termination\":" + number(h[DeviceHealth::MediaTermination]) +
      ",\"cancellationTail\":\"unmeasured\"}}";
  return json.size() <= MaximumJsonBytes ? json : "";
}

} }

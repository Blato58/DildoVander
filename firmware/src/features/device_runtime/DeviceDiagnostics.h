#pragma once

#include <array>
#include <atomic>
#include <string>
#include "RuntimeDiagnostics.h"

namespace kufr { namespace runtime {

enum class StationPhase : uint32_t { Unknown, Offline, Connecting, Connected };
enum class OtaPhase : uint32_t {
  Unknown, Disabled, Idle, VerifyingBoot, Checking, Available, Current, WaitingIdle,
  Downloading, VerifyingImage, ReadyToReboot, Rebooting, Rollback, FinalizingBoot, ServiceRequired, Error
};

// Fixed redacted fields only. No device credentials, SSIDs, operation IDs,
// journal records, media, visitor information or provider messages can enter.
struct DeviceHealth final {
  enum Field : size_t {
    SampledAtLow, SampledAtHigh, ResetReason, Profile, CalibrationConfigured,
    HardwareAvailable, Station, ActiveProfile, HasIp, StationChanging, BackendOnline,
    Ota, StorageStartup, StorageState, JournalKnown, JournalHealthy, JournalUsed,
    JournalEpochLow, JournalEpochHigh, CompactionsLow, CompactionsHigh, StorageBusy,
    InternalFree, InternalMinimum, InternalLargest, PsramFree, PsramMinimum, PsramLargest,
    ControlStackMinimum, ServiceStackMinimum, StorageStackMinimum, StorageMaximumOperationUs,
    UrgentStopPending, Authentication, TrustedTime, MediaAvailable,
    MediaSessionLow, MediaSessionHigh, MediaCancellationLow, MediaCancellationHigh,
    MediaPlayedLow, MediaPlayedHigh, CaptureQueuedBytes, CaptureHighWaterBytes, CaptureOverruns,
    PlaybackQueuedBytes, PlaybackHighWaterBytes, MediaTermination, Count
  };
  std::array<uint32_t, Count> values{};
  uint32_t& operator[](Field field) { return values[field]; }
  uint32_t operator[](Field field) const { return values[field]; }
};

class DeviceHealthMailbox final {
 public:
  void Publish(const DeviceHealth& health) {
    generation_.fetch_add(1);
    for (size_t index = 0; index < DeviceHealth::Count; ++index) words_[index].store(health.values[index]);
    generation_.fetch_add(1);
  }
  bool Read(DeviceHealth& health) const {
    const auto before = generation_.load();
    if (!before || (before & 1)) return false;
    DeviceHealth next;
    for (size_t index = 0; index < DeviceHealth::Count; ++index) next.values[index] = words_[index].load();
    if (before != generation_.load()) return false;
    health = next;
    return true;
  }
 private:
  std::atomic<uint32_t> generation_{0};
  std::array<std::atomic<uint32_t>, DeviceHealth::Count> words_{};
};

struct DeviceDiagnosticSnapshot final { RuntimeSnapshot runtime; DeviceHealth health; };

class RuntimeDiagnosticsPort {
 public:
  virtual ~RuntimeDiagnosticsPort() = default;
  virtual bool ReadDiagnostics(DeviceDiagnosticSnapshot& snapshot) const = 0;
  std::string Json() const;
  static std::string Serialize(const DeviceDiagnosticSnapshot& snapshot, const char* buildId);
  static constexpr size_t MaximumJsonBytes = 2048;
};

} }

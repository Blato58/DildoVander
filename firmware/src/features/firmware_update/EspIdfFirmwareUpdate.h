#pragma once

#include <array>
#include <cstdint>
#include <cstddef>
#include <string>
#include "OtaManifest.h"
#include "OtaPolicy.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"

namespace kufr { namespace ota {

struct StatusSnapshot final {
  std::string state;
  std::string version;
  std::string availableVersion;
  std::string error;
  bool automaticInstall = false;
};

// Lifetime must cover app lifetime. All public APIs are thread-safe; Tick is
// called only by the control owner, which enforces Reserved in every admission.
class EspIdfFirmwareUpdate final {
 public:
  bool Begin();
  void Tick(uint64_t nowMs, bool networkReady, bool selfTestReady, bool safeIdle);
  bool RequestCheck();
  bool RequestInstall();
  bool SetAutomaticInstall(bool enabled);
  bool SetCheckIntervalSeconds(uint32_t seconds);
  // Settings return true only after a matching worker ACK is observed. Repeat
  // the same call while LastOperationIsPending(); conflicting values are refused.
  bool LastOperationIsPending() const;
  StatusSnapshot Status() const;
  bool Reserved() const;

 private:
  friend struct FirmwareUpdateTestAccess;
  enum class Work { None, Check, Install, ConfirmBoot, Rollback, Reboot, Settings };
  enum class GuardRead { Missing, Present, Invalid, Unavailable };
  enum class Setting { AutomaticInstall, CheckInterval };
  enum class SettingResult { Pending, Succeeded, Failed };
  struct SettingsIntent {
    bool active = false;
    Setting kind = Setting::AutomaticInstall;
    uint32_t value = 0;
    SettingResult result = SettingResult::Pending;
  };
  static void WorkerEntry(void* owner);
  void Worker();
  void ProcessWork();
  void ConfirmBoot();
  GuardRead LoadConfirmationGuard();
  bool PersistConfirmationGuard(bool erase);
  void Rollback();
  void StoreSettings();
  bool SetSetting(Setting kind, uint32_t value);
  void Check();
  void Install();
  void Fail(const char* error);
  bool LoadSettings();
  bool VerifyEnvelope(const Envelope& envelope) const;
  bool VerifyPartition(const void* partition, const Manifest& manifest, uint8_t* buffer, size_t bufferSize) const;

  mutable SemaphoreHandle_t mutex_ = nullptr;
  TaskHandle_t task_ = nullptr;
  OtaPolicy policy_;
  StatusSnapshot status_;
  Manifest manifest_;
  std::string origin_;
  std::string channel_;
  std::string trustPem_;
  Work work_ = Work::None;
  bool beginAttempted_ = false;
  bool configured_ = false;
  bool checkRequested_ = false;
  bool installRequested_ = false;
  bool reserved_ = false;
  bool bootPending_ = false;
  // The seal authorizes exactly one guard erase after a timely SDK ACK. Once
  // sealed, later control health/deadline changes cannot invalidate that erase.
  bool finalizationAuthorized_ = false;
  bool confirmationBlocked_ = false;
  std::array<uint8_t, 48> confirmationGuard_{};
  std::array<char, 10> confirmationGuardKey_{};
  bool rollbackAllowed_ = false;
  bool rollbackRequested_ = false;
  bool rollbackAttempted_ = false;
  bool lastOperationPending_ = false;
  uint64_t bootDeadlineMs_ = 0;
  SettingsIntent settings_;
};
} }  // namespace kufr::ota

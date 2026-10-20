#pragma once

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
  StatusSnapshot Status() const;
  bool Reserved() const;

 private:
  friend struct FirmwareUpdateTestAccess;
  enum class Work { None, Check, Install };
  static void WorkerEntry(void* owner);
  void Worker();
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
  bool configured_ = false;
  bool checkRequested_ = false;
  bool installRequested_ = false;
  bool reserved_ = false;
  bool bootPending_ = false;
};
} }  // namespace kufr::ota

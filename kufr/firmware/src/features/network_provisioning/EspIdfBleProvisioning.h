#pragma once

#include <atomic>
#include "cJSON.h"
#include "protocomm.h"
#include "../backend_access/EspIdfStationNetwork.h"

// Callbacks run on BLE transport task; use atomics/queues, never owner state.
struct BleManagementCallbacks {
  void (*status)(cJSON* response, void* context) = nullptr;
  bool (*command)(const char* name, bool automaticInstall, void* context) = nullptr;
  void* context = nullptr;
};

class EspIdfBleProvisioning final {
 public:
  explicit EspIdfBleProvisioning(EspIdfStationNetwork& station) : station_(station) {}
  bool Begin(bool nvsInitialized, const char* deviceId, const char* firmwareVersion,
             BleManagementCallbacks callbacks);
  void Tick(uint64_t nowMs, bool networkReady);
  bool Active() const { return active_.load(); }

 private:
  bool Open(uint64_t nowMs, uint64_t windowDeadline);
  void Close(uint64_t nowMs);
  void UpdateWindow(uint64_t nowMs, bool networkReady);
  static void TaskEntry(void* context);
  void Run();
  static esp_err_t Handle(uint32_t session, const uint8_t* input, ssize_t inputLength,
                          uint8_t** output, ssize_t* outputLength, void* context);
  cJSON* Execute(cJSON* request);
  void AddStatus(cJSON* response);

  EspIdfStationNetwork& station_;
  BleManagementCallbacks callbacks_;
  protocomm_t* transport_ = nullptr;
  char proof_[33]{};
  protocomm_security1_params_t proofParams_{};
  char deviceId_[65]{};
  char firmwareVersion_[65]{};
  char name_[30]{};
  std::atomic<bool> active_{false};
  std::atomic<uint64_t> deadline_{0};
  uint64_t offlineSince_ = 0;
  bool offlineTracked_ = false;
  bool recoveryUsed_ = false;
  bool initialPending_ = false;
  uint64_t initialDeadline_ = 0;
  bool closing_ = false;
  uint64_t nextCloseRetry_ = 0;
  uint64_t nextOpenAttempt_ = 0;
  bool ready_ = false;
  TaskHandle_t lifecycleTask_ = nullptr;
  std::atomic<bool> networkReady_{false};
};

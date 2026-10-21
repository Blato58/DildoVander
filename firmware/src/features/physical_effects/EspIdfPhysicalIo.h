#pragma once

#include <atomic>
#include "driver/i2c_master.h"
#include "driver/rmt_tx.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "PhysicalEffects.h"
#include "../voice_turn/AudioBoardPort.h"
#include "../device_runtime/PhysicalIoBudget.h"

struct PhysicalIoTiming final {
  uint32_t maximumTransferUs = 0;
  uint32_t maximumPassBusUs = 0;
  uint32_t lockRefusals = 0;
  uint32_t budgetRefusals = 0;
  uint32_t coalescedLedFrames = 0;
  uint32_t overBudgetPasses = 0;
};

class EspIdfPhysicalIo final : public PhysicalIo, public AudioBoardPort {
 public:
  bool Initialize(bool nvsReady);
  bool LoadCalibration(std::array<ServoCalibration, 6>& calibration, uint64_t& revision);
  bool Available() const { return available_ && !failed_.load(); }
  bool ProfileSelected() const { return profileSelected_; }
  bool AudioReady() const override { return Available(); }
  void RequestUrgentStop() override;
  uint32_t StopEpoch() const override { return stopEpoch_.load(); }
  bool ClearUrgentStopAfterInspection(uint32_t expectedEpoch) override;
  bool UrgentStopPending() const { return (stopEpoch_.load() & 1u) != 0; }
  // Called by the sole control owner. Startup has a separate per-transfer cap.
  void BeginControlPass(uint64_t nowUs);
  PhysicalIoTiming Timing() const { return timing_; }
  void EnableOutputs(bool enabled) override;
  bool FullOff() override;
  bool Servo(uint8_t channel, uint16_t pulseUs) override;
  bool Vibration(uint8_t channel, bool enabled) override;
  bool Lights(const std::array<uint8_t, 108>& grb) override;
  bool ReadInputs(PhysicalInputs& inputs) override;
 private:
  bool Write(i2c_master_dev_handle_t device, uint8_t reg, const uint8_t* bytes, size_t size);
  bool Read(i2c_master_dev_handle_t device, uint8_t reg, uint8_t* bytes, size_t size);
  bool Fail();
  bool TakeBus();
  bool FlushLights();
  int TransferTimeout();
  void RecordTransfer(uint64_t startedAtUs);
  i2c_master_bus_handle_t bus_ = nullptr;
  i2c_master_dev_handle_t pca_ = nullptr, mcp_ = nullptr;
  rmt_channel_handle_t rgb_ = nullptr;
  rmt_encoder_handle_t encoder_ = nullptr;
  SemaphoreHandle_t mutex_ = nullptr;
  std::array<rmt_symbol_word_t, 865> symbols_{};
  std::array<uint8_t, 108> pendingGrb_{}, submittedGrb_{};
  bool lightPending_ = false, lightInFlight_ = false, lightSubmitted_ = false;
  bool channelsReset_ = false;
  uint16_t dirtyChannels_ = 0;
  kufr::runtime::PhysicalIoBudget budget_;
  PhysicalIoTiming timing_;
  uint32_t passBusUs_ = 0;
  bool passOverBudget_ = false;
  std::atomic<bool> failed_{false};
  std::atomic<uint32_t> stopEpoch_{0};
  portMUX_TYPE outputMux_ = portMUX_INITIALIZER_UNLOCKED;
  bool available_ = false, profileSelected_ = false, profileVerified_ = false;
};

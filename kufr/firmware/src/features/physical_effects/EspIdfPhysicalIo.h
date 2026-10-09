#pragma once

#include <atomic>
#include "driver/i2c_master.h"
#include "driver/rmt_tx.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "PhysicalEffects.h"
#include "../voice_turn/AudioBoardPort.h"

class EspIdfPhysicalIo final : public PhysicalIo, public AudioBoardPort {
 public:
  bool Initialize(bool nvsReady);
  bool LoadCalibration(std::array<ServoCalibration, 6>& calibration, uint64_t& revision);
  bool Available() const { return available_ && !failed_.load(); }
  bool ProfileSelected() const { return profileSelected_; }
  bool AudioReady() const override { return Available(); }
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
  i2c_master_bus_handle_t bus_ = nullptr;
  i2c_master_dev_handle_t pca_ = nullptr, mcp_ = nullptr;
  rmt_channel_handle_t rgb_ = nullptr;
  rmt_encoder_handle_t encoder_ = nullptr;
  SemaphoreHandle_t mutex_ = nullptr;
  std::array<rmt_symbol_word_t, 865> symbols_{};
  std::atomic<bool> failed_{false};
  bool available_ = false, profileSelected_ = false, profileVerified_ = false;
};

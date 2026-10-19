#include "PcaRegisters.h"

#include <cstdio>
#include "driver/gpio.h"
#include "driver/i2c_master.h"
#include "driver/uart.h"
#include "esp_err.h"
#include "esp_rom_sys.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

namespace {
constexpr int TransferTimeoutMs = 50;
// At 100 Hz, a one-tick wait can expire at the immediately following tick.
constexpr int ReadTimeoutMs = 20;
static_assert(pdMS_TO_TICKS(ReadTimeoutMs) >= 2, "I2C reads need at least two RTOS ticks");

class PcaOutputs final : public ServoBench::Outputs, private ServoBench::RegisterBus {
 public:
  bool Initialize() {
    i2c_master_bus_config_t config{};
    config.i2c_port = I2C_NUM_0;
    config.sda_io_num = GPIO_NUM_1;
    config.scl_io_num = GPIO_NUM_14;
    config.clk_source = I2C_CLK_SRC_DEFAULT;
    config.glitch_ignore_cnt = 7;
    if (i2c_new_master_bus(&config, &bus_) != ESP_OK) return false;
    i2c_device_config_t device{};
    device.dev_addr_length = I2C_ADDR_BIT_LEN_7;
    device.device_address = 0x40;
    device.scl_speed_hz = 100000;
    return i2c_master_probe(bus_, 0x40, TransferTimeoutMs) == ESP_OK &&
        i2c_master_bus_add_device(bus_, &device, &device_) == ESP_OK && Prepare();
  }

  bool Disable() override { return ServoBench::DisableAllPca(*this); }

  bool Prepare() override {
    return ServoBench::PreparePca(*this);
  }

  bool Pulse(unsigned channel, unsigned ticks) override {
    ServoBench::PwmSnapshot snapshot;
    const auto result = ServoBench::SetVerifiedPulse(*this, channel, ticks, snapshot);
    if (result == ServoBench::PulseReadbackResult::WriteFailed) return false;
    if (result == ServoBench::PulseReadbackResult::ReadFailed) {
      Report("PCA_READBACK failed; stopping and latching fault");
      return false;
    }
    const bool matches = result == ServoBench::PulseReadbackResult::Match;
    std::printf("SERVO_BENCH PCA_READBACK ch=%u mode1=0x%02x mode2=0x%02x "
                "prescale=%u on=%u off=%u full_on=%u full_off=%u expected_off=%u match=%u\n",
                channel, snapshot.mode1, snapshot.mode2, snapshot.prescale,
                snapshot.OnTicks(), snapshot.OffTicks(), static_cast<unsigned>(snapshot.FullOn()),
                static_cast<unsigned>(snapshot.FullOff()), ticks, static_cast<unsigned>(matches));
    return matches;
  }

  void Report(const char* message) override {
    std::printf("SERVO_BENCH %s\n", message);
    // Reports follow the controller's shutdown attempt. Never print from an
    // unsuccessful transfer before the caller has had a chance to stop PWM.
    for (std::size_t i = 0; i < failureCount_; ++i) {
      const auto& failure = failures_[i];
      std::printf("SERVO_BENCH I2C_ERROR op=%s addr=0x40 reg=0x%02x "
                  "code=0x%x name=%s sda=%d scl=%d\n",
                  failure.operation, failure.reg, static_cast<unsigned>(failure.error),
                  esp_err_to_name(failure.error), failure.sda, failure.scl);
    }
    failureCount_ = 0;
  }

 private:
  struct BusFailure {
    const char* operation = nullptr;
    uint8_t reg = 0;
    esp_err_t error = ESP_OK;
    int sda = 0;
    int scl = 0;
  };

  bool RecordTransfer(const char* operation, uint8_t reg, esp_err_t result) {
    if (result == ESP_OK) return true;
    if (failureCount_ < sizeof(failures_) / sizeof(failures_[0])) {
      failures_[failureCount_++] = {
          operation, reg, result, gpio_get_level(GPIO_NUM_1), gpio_get_level(GPIO_NUM_14)};
    }
    return false;
  }

  bool Write(uint8_t reg, const uint8_t* values, std::size_t size) override {
    uint8_t bytes[65]{};
    if (!device_ || size > 64) return false;
    bytes[0] = reg;
    std::memcpy(bytes + 1, values, size);
    return RecordTransfer("WRITE", reg,
        i2c_master_transmit(device_, bytes, size + 1, TransferTimeoutMs));
  }

  bool Read(uint8_t reg, uint8_t& value) override {
    return device_ && RecordTransfer("READ", reg, i2c_master_transmit_receive(
        device_, &reg, 1, &value, 1, ReadTimeoutMs));
  }

  void WaitOscillator() override { esp_rom_delay_us(500); }

  i2c_master_bus_handle_t bus_ = nullptr;
  i2c_master_dev_handle_t device_ = nullptr;
  BusFailure failures_[4]{};
  std::size_t failureCount_ = 0;
};
}  // namespace

extern "C" void app_main() {
  PcaOutputs outputs;
  const bool ready = outputs.Initialize();
  ServoBench::Controller controller(outputs);
  // Keep UART0's console pin mapping; use USB-UART, not native USB-OTG.
  const bool uartReady = uart_driver_install(UART_NUM_0, 256, 0, 0, nullptr, 0) == ESP_OK;
  if (!ready || !uartReady) controller.Fault();
  if (ready && uartReady && !controller.Faulted()) {
    char message[192];
    std::snprintf(message, sizeof(message),
        "READY PWM FULL_OFF; GPIO1=SDA GPIO14=SCL no OE control PCA=0x40 nominal 50Hz; "
        "diag=i2c-errors-v2 read_timeout_ms=20; calibration=v2 "
        "calibration_min_us=%u calibration_max_us=%u; type help",
        ServoBench::CalibrationMinimumUs, ServoBench::CalibrationMaximumUs);
    outputs.Report(message);
  } else {
    outputs.Report("NOT READY; PWM state not guaranteed; cut servo power, check wiring and restart");
  }
  for (;;) {
    controller.Tick(static_cast<uint64_t>(esp_timer_get_time()));
    uint8_t byte = 0;
    const int count = uartReady ? uart_read_bytes(UART_NUM_0, &byte, 1, pdMS_TO_TICKS(10)) : -1;
    if (count == 1) {
      controller.Feed(static_cast<char>(byte), static_cast<uint64_t>(esp_timer_get_time()));
    } else if (count < 0) {
      controller.Fault();
      vTaskDelay(pdMS_TO_TICKS(1000));
    }
  }
}

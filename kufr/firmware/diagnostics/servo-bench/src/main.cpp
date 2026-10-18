#include "PcaRegisters.h"

#include <cstdio>
#include "driver/gpio.h"
#include "driver/i2c_master.h"
#include "driver/uart.h"
#include "esp_rom_sys.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

namespace {
constexpr gpio_num_t OePin = GPIO_NUM_2;
constexpr int TransferTimeoutMs = 50;

class PcaOutputs final : public ServoBench::Outputs, private ServoBench::RegisterBus {
 public:
  bool Initialize() {
    // Preload the latch before enabling the output driver. External pull-up
    // must hold OE HIGH throughout ROM boot/reset, before this code can run.
    if (gpio_set_level(OePin, 1) != ESP_OK ||
        gpio_set_direction(OePin, GPIO_MODE_OUTPUT) != ESP_OK) return false;
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

  void Disable() override { gpio_set_level(OePin, 1); }

  bool Prepare() override {
    Disable();
    return ServoBench::PreparePca(*this);
  }

  bool Pulse(unsigned channel, unsigned ticks) override {
    Disable();
    if (!ServoBench::SetPulse(*this, channel, ticks)) return false;
    return gpio_set_level(OePin, 0) == ESP_OK;
  }

  void Report(const char* message) override { std::printf("SERVO_BENCH %s\n", message); }

 private:
  bool Write(uint8_t reg, const uint8_t* values, std::size_t size) override {
    uint8_t bytes[65]{};
    if (!device_ || size > 64) return false;
    bytes[0] = reg;
    std::memcpy(bytes + 1, values, size);
    return i2c_master_transmit(device_, bytes, size + 1, TransferTimeoutMs) == ESP_OK;
  }

  bool Read(uint8_t reg, uint8_t& value) override {
    return device_ && i2c_master_transmit_receive(
        device_, &reg, 1, &value, 1, TransferTimeoutMs) == ESP_OK;
  }

  void WaitOscillator() override { esp_rom_delay_us(500); }

  i2c_master_bus_handle_t bus_ = nullptr;
  i2c_master_dev_handle_t device_ = nullptr;
};
}  // namespace

extern "C" void app_main() {
  PcaOutputs outputs;
  const bool ready = outputs.Initialize();
  ServoBench::Controller controller(outputs);
  // Keep UART0's console pin mapping; use USB-UART, not native USB-OTG.
  const bool uartReady = uart_driver_install(UART_NUM_0, 256, 0, 0, nullptr, 0) == ESP_OK;
  if (!ready || !uartReady) controller.Fault();
  outputs.Report(ready && uartReady ?
      "READY outputs disabled; GPIO1=SDA GPIO14=SCL GPIO2=OE PCA=0x40 nominal 50Hz; type help" :
      "NOT READY outputs disabled; check wiring and restart");
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

#include "../src/PcaRegisters.h"
#include <cassert>
#include <cstdio>
#include <string>

struct FakeOutputs : ServoBench::Outputs {
  bool enabled = false;
  bool failPrepare = false;
  bool failPulse = false;
  bool failDisable = false;
  std::string lastReport;
  unsigned pulses = 0;
  unsigned channel = 99;
  unsigned ticks = 0;
  bool Disable() override {
    if (failDisable) return false;
    enabled = false;
    return true;
  }
  bool Prepare() override { assert(!enabled); return !failPrepare; }
  bool Pulse(unsigned selected, unsigned width) override {
    assert(!enabled);
    ++pulses;
    channel = selected;
    ticks = width;
    enabled = !failPulse;
    return !failPulse;
  }
  void Report(const char* message) override { lastReport = message; }
};

static void Send(ServoBench::Controller& controller, const std::string& line, uint64_t now = 0) {
  for (char byte : line) controller.Feed(byte, now);
}

struct FakeRegisterBus : ServoBench::RegisterBus {
  uint8_t registers[256]{};
  unsigned operations = 0;
  unsigned failAt = 0;
  bool corruptReadback = false;
  bool Write(uint8_t reg, const uint8_t* values, std::size_t size) override {
    if (++operations == failAt) return false;
    assert(reg + size <= 256);
    const bool autoIncrement = (registers[0] & 0x20) != 0;
    for (std::size_t i = 0; i < size; ++i) {
      const unsigned address = reg + (autoIncrement ? static_cast<unsigned>(i) : 0);
      registers[address] = values[i];
      // Datasheet: ALL_LED_* writes fill the respective registers on all channels.
      if (address >= 0xfa && address <= 0xfd) {
        for (unsigned channel = 0; channel < 16; ++channel)
          registers[0x06 + channel * 4 + (address - 0xfa)] = values[i];
      }
    }
    return true;
  }
  bool Read(uint8_t reg, uint8_t& value) override {
    if (++operations == failAt) return false;
    value = corruptReadback ? 0xff : registers[reg];
    return true;
  }
  void WaitOscillator() override {}
};

static void CheckRegisters() {
  FakeRegisterBus bus;
  // Stale full-on bits and pulse widths must be replaced, including channel15.
  std::memset(bus.registers, 0xff, sizeof(bus.registers));
  assert(ServoBench::PreparePca(bus));
  assert(bus.registers[0] == 0x20 && bus.registers[1] == 0x04 && bus.registers[0xfe] == 121);
  for (unsigned channel = 0; channel < 16; ++channel) {
    const unsigned reg = 0x06 + channel * 4;
    assert(bus.registers[reg] == 0 && bus.registers[reg + 1] == 0);
    assert(bus.registers[reg + 2] == 0 && bus.registers[reg + 3] == 0x10);
  }
  for (unsigned selected = 0; selected < 6; ++selected) {
    assert(ServoBench::SetPulse(bus, selected, ServoBench::PulseTicks(1500)));
    for (unsigned channel = 0; channel < 16; ++channel) {
      const unsigned reg = 0x06 + channel * 4;
      assert(bus.registers[reg + 3] == (channel == selected ? 1 : 0x10));
      assert(bus.registers[reg + 2] == (channel == selected ? 51 : 0));
    }
  }
  assert(!ServoBench::SetPulse(bus, 6, 307));
  assert(!ServoBench::SetPulse(bus, 0, 4096));
  for (unsigned failAt = 1; failAt <= 9; ++failAt) {
    FakeRegisterBus failed;
    failed.failAt = failAt;
    assert(!ServoBench::PreparePca(failed));
    assert(failed.operations == failAt); // First failed transfer aborts initialization.
  }
  bus.corruptReadback = true;
  assert(!ServoBench::PreparePca(bus));
}

struct RegisterOutputs : ServoBench::Outputs {
  FakeRegisterBus bus;
  std::string lastReport;
  bool Disable() override { return ServoBench::DisableAllPca(bus); }
  bool Prepare() override { return ServoBench::PreparePca(bus); }
  bool Pulse(unsigned channel, unsigned ticks) override { return ServoBench::SetPulse(bus, channel, ticks); }
  void Report(const char* message) override { lastReport = message; }
};

static void AssertAllOff(const FakeRegisterBus& bus) {
  for (unsigned channel = 0; channel < 16; ++channel)
    assert((bus.registers[0x09 + channel * 4] & 0x10) != 0);
}

static void CheckPcaStop() {
  FakeRegisterBus stale;
  for (unsigned channel = 0; channel < 16; ++channel)
    stale.registers[0x07 + channel * 4] = 0x10; // Stale FULL_ON; no MODE1 auto-increment.
  assert(ServoBench::DisableAllPca(stale));
  assert(stale.operations == 1 && stale.registers[0xfd] == 0x10);
  AssertAllOff(stale);

  for (const char* stop : {"stop\n", "!", "timeout"}) {
    RegisterOutputs outputs;
    ServoBench::Controller controller(outputs);
    AssertAllOff(outputs.bus);
    Send(controller, "arm\npulse 0 1500\n", 100);
    assert((outputs.bus.registers[0x09] & 0x10) == 0);
    if (std::strcmp(stop, "timeout") == 0) controller.Tick(500100);
    else Send(controller, stop, 200);
    AssertAllOff(outputs.bus); // Real register algorithm, no fake enabled flag.
    assert(!controller.Faulted());
  }

  for (bool timeout : {true, false}) {
    RegisterOutputs outputs;
    ServoBench::Controller controller(outputs);
    Send(controller, "arm\npulse 0 1500\n", 100);
    outputs.bus.failAt = outputs.bus.operations + 1;
    if (timeout) controller.Tick(500100);
    else Send(controller, "!", 200);
    assert(controller.Faulted());
    assert((outputs.bus.registers[0x09] & 0x10) == 0); // Failed I2C cannot promise a stop.
    assert(outputs.lastReport.find("PWM_OFF_FAILED") != std::string::npos);
    Send(controller, "\nstop\n", 500200); // A subsequent explicit Stop may retry.
    AssertAllOff(outputs.bus);
    Send(controller, "arm\npulse 0 1500\n", 500300);
    AssertAllOff(outputs.bus); // Fault stays latched even when bus recovers.
  }
  FakeOutputs failed;
  failed.failDisable = true;
  ServoBench::Controller controller(failed);
  assert(controller.Faulted());
  Send(controller, "arm\npulse 0 1500\n");
  assert(failed.pulses == 0);

  FakeOutputs armFailure;
  ServoBench::Controller armedController(armFailure);
  armFailure.failDisable = true;
  Send(armedController, "arm\npulse 0 1500\n");
  assert(armedController.Faulted() && armFailure.pulses == 0);
}

int main() {
  CheckRegisters();
  CheckPcaStop();
  {
    FakeOutputs outputs;
    ServoBench::Controller controller(outputs);
    assert(!outputs.enabled);
    Send(controller, "pulse 0 1500\n");
    assert(outputs.pulses == 0);
    Send(controller, "arm\r\npulse 0 1500\r\n", 100);
    assert(outputs.enabled && outputs.pulses == 1 && outputs.channel == 0);
    assert(outputs.ticks == 307);
    controller.Tick(500099);
    assert(outputs.enabled);
    controller.Tick(500100);
    assert(!outputs.enabled);
    Send(controller, "pulse 0 1500\n", 500101);
    assert(outputs.pulses == 1); // Every pulse consumes arm.
  }
  {
    FakeOutputs outputs;
    ServoBench::Controller controller(outputs);
    Send(controller, "arm\n");
    Send(controller, "pulse 0 1500\n", ServoBench::ArmDurationUs);
    assert(outputs.pulses == 0); // Expiry checked before buffered input.
    Send(controller, "arm\npulse 5 1600\n", 20000000);
    assert(outputs.enabled && outputs.channel == 5 && outputs.ticks == 328);
    Send(controller, "pulse 0 1500\n", 20000001);
    assert(!outputs.enabled && outputs.pulses == 1); // Busy never queues.
  }
  const char* invalid[] = {
    "pulse 6 1500\n", "pulse 16 1500\n", "pulse -1 1500\n",
    "pulse 0 1399\n", "pulse 0 1601\n", "pulse 0 1500 extra\n",
    "pulse 0 1e03\n", "pulse 0 +500\n", "pulse 0 9999999999999999\n",
    "pulse 0 1500 \n", "unknown\n", "pulse 0 15\t0\n"
  };
  for (const char* line : invalid) {
    FakeOutputs outputs;
    ServoBench::Controller controller(outputs);
    Send(controller, "arm\n");
    Send(controller, line);
    Send(controller, "pulse 0 1500\n");
    assert(!outputs.enabled && outputs.pulses == 0);
  }
  {
    FakeOutputs outputs;
    ServoBench::Controller controller(outputs);
    Send(controller, "arm\npulse 0 1400\n");
    assert(outputs.enabled && outputs.ticks == 287);
    Send(controller, "partial!"); // No newline needed for emergency stop.
    assert(!outputs.enabled);
    Send(controller, "arm\npulse 0 1500\n"); // Remainder of interrupted line discarded.
    assert(outputs.pulses == 1);
    Send(controller, "arm\npulse 0 1500\n");
    Send(controller, "stop\n");
    assert(!outputs.enabled && outputs.pulses == 2);
    Send(controller, "arm\n" + std::string(60, 'x') + "arm\npulse 0 1500\n");
    assert(!outputs.enabled && outputs.pulses == 2);
  }
  for (bool preparationFailure : {true, false}) {
    FakeOutputs outputs;
    outputs.failPrepare = preparationFailure;
    outputs.failPulse = !preparationFailure;
    ServoBench::Controller controller(outputs);
    Send(controller, "arm\npulse 0 1500\n");
    const unsigned attempts = outputs.pulses;
    outputs.failPrepare = outputs.failPulse = false;
    Send(controller, "arm\npulse 0 1500\n");
    assert(!outputs.enabled && outputs.pulses == attempts); // Fault latched until reboot.
  }
  std::puts("servo-bench controller: PASS");
}

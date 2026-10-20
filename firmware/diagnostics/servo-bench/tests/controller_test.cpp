#include "../../../src/diagnostics/servo-bench/PcaRegisters.h"
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
  unsigned fullOffWrites = 0;
  bool Write(uint8_t reg, const uint8_t* values, std::size_t size) override {
    if (++operations == failAt) return false;
    if (reg == 0xfd && size == 1 && values[0] == 0x10) ++fullOffWrites;
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
    ServoBench::PwmSnapshot snapshot;
    assert(ServoBench::ReadPwmSnapshot(bus, selected, snapshot));
    assert(snapshot.Matches(307) && snapshot.OnTicks() == 0 && snapshot.OffTicks() == 307);
    assert(!snapshot.FullOn() && !snapshot.FullOff());
    for (unsigned channel = 0; channel < 16; ++channel) {
      const unsigned reg = 0x06 + channel * 4;
      assert(bus.registers[reg + 3] == (channel == selected ? 1 : 0x10));
      assert(bus.registers[reg + 2] == (channel == selected ? 51 : 0));
    }
  }
  assert(!ServoBench::SetPulse(bus, 6, 307));
  assert(!ServoBench::SetPulse(bus, 0, 0));
  assert(!ServoBench::SetPulse(bus, 0, ServoBench::PulseTicks(ServoBench::MaximumCommandPulseUs) + 1));
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

static void CheckReadback() {
  for (unsigned failAt = 1; failAt <= 7; ++failAt) {
    FakeRegisterBus bus;
    bus.failAt = failAt;
    ServoBench::PwmSnapshot snapshot;
    assert(!ServoBench::ReadPwmSnapshot(bus, 0, snapshot));
    assert(bus.operations == failAt);
  }
  FakeRegisterBus bus;
  ServoBench::PwmSnapshot snapshot;
  assert(!ServoBench::ReadPwmSnapshot(bus, 6, snapshot) && bus.operations == 0);
  assert(ServoBench::PreparePca(bus));
  for (unsigned microseconds : {500U, 1000U, 1400U, 1500U, 1600U, 2000U, 2400U}) {
    const unsigned ticks = ServoBench::PulseTicks(microseconds);
    assert(ServoBench::SetPulse(bus, 0, ticks));
    assert(ServoBench::ReadPwmSnapshot(bus, 0, snapshot) && snapshot.Matches(ticks));
    for (unsigned field = 0; field < 7; ++field) {
      auto corrupted = snapshot;
      switch (field) {
        case 0: corrupted.mode1 |= 0x10; break; // Sleep.
        case 1: corrupted.mode2 ^= 0x10; break; // Inverted output.
        case 2: ++corrupted.prescale; break;
        case 3: ++corrupted.onLow; break;
        case 4: corrupted.onHigh |= 0x10; break;
        case 5: ++corrupted.offLow; break;
        case 6: corrupted.offHigh |= 0x10; break;
      }
      assert(!corrupted.Matches(ticks));
    }
    snapshot.mode1 |= 0x80;
    assert(snapshot.Matches(ticks));
  }
  bus.corruptReadback = true;
  assert(ServoBench::ReadPwmSnapshot(bus, 0, snapshot) && !snapshot.Matches(307));
}

struct RegisterOutputs : ServoBench::Outputs {
  FakeRegisterBus bus;
  std::string lastReport;
  bool Disable() override { return ServoBench::DisableAllPca(bus); }
  bool Prepare() override { return ServoBench::PreparePca(bus); }
  bool Pulse(unsigned channel, unsigned ticks) override {
    ServoBench::PwmSnapshot snapshot;
    return ServoBench::SetVerifiedPulse(bus, channel, ticks, snapshot) ==
        ServoBench::PulseReadbackResult::Match;
  }
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

static void CheckVerifiedPulseFailure() {
  for (unsigned failedRead = 1; failedRead <= 7; ++failedRead) {
    RegisterOutputs outputs;
    ServoBench::Controller controller(outputs);
    Send(controller, "arm\n", 0);
    const unsigned previousOffWrites = outputs.bus.fullOffWrites;
    outputs.bus.failAt = outputs.bus.operations + 3 + failedRead;
    Send(controller, "pulse 0 1500\n", 100);
    assert(controller.Faulted());
    AssertAllOff(outputs.bus);
    // One stop before the pulse, one after read failure and the controller retry.
    assert(outputs.bus.fullOffWrites == previousOffWrites + 3);
    Send(controller, "arm\npulse 0 1500\n", 200);
    AssertAllOff(outputs.bus);
  }
  RegisterOutputs outputs;
  ServoBench::Controller controller(outputs);
  Send(controller, "arm\n", 0);
  const unsigned previousOffWrites = outputs.bus.fullOffWrites;
  outputs.bus.corruptReadback = true;
  Send(controller, "pulse 0 1500\n", 100);
  assert(controller.Faulted());
  AssertAllOff(outputs.bus);
  assert(outputs.bus.fullOffWrites == previousOffWrites + 3);
}

static void CheckCalibrationTiming() {
  for (unsigned milliseconds : {500U, 1750U, 3000U}) {
    for (unsigned channel : {0U, 5U}) {
      FakeOutputs outputs;
      ServoBench::Controller controller(outputs);
      Send(controller, "arm\n", 10);
      char command[24];
      std::snprintf(command, sizeof(command), "cal %u %04u %04u",
          channel, ServoBench::CalibrationMaximumUs, milliseconds);
      Send(controller, command, 100);
      assert(outputs.pulses == 0); // A complete line is required before movement.
      const uint64_t commandTime = ServoBench::ArmDurationUs - 1;
      Send(controller, "\r\n", commandTime);
      assert(outputs.enabled && outputs.channel == channel &&
          outputs.ticks == ServoBench::PulseTicks(ServoBench::CalibrationMaximumUs));
      assert(outputs.lastReport == "PULSE nominal " + std::to_string(milliseconds) +
          " ms; calibration; timeout/Stop writes PCA FULL_OFF");
      const uint64_t deadline = commandTime + static_cast<uint64_t>(milliseconds) * 1000;
      controller.Tick(deadline - 1);
      assert(outputs.enabled);
      controller.Tick(deadline);
      assert(!outputs.enabled);
      assert(outputs.lastReport == "DONE PWM FULL_OFF; arm again for another pulse");
      Send(controller, "cal 0 1000 0500\n", deadline + 1);
      assert(outputs.pulses == 1); // Calibration consumes exactly one arm too.
    }
  }
  FakeOutputs outputs;
  ServoBench::Controller controller(outputs);
  Send(controller, "cal 0 1000 0500\n");
  assert(outputs.pulses == 0);
  Send(controller, "arm\n", 10);
  Send(controller, "cal 0 1000 0500\n", 10 + ServoBench::ArmDurationUs);
  assert(outputs.pulses == 0); // Exact arm expiry is checked before the command.
  char minimumCommand[32];
  std::snprintf(minimumCommand, sizeof(minimumCommand), "arm\ncal 0 %04u 0500\n",
      ServoBench::CalibrationMinimumUs);
  Send(controller, minimumCommand, 20000000);
  assert(outputs.enabled && outputs.ticks == ServoBench::PulseTicks(ServoBench::CalibrationMinimumUs));
  char maximumCommand[32];
  std::snprintf(maximumCommand, sizeof(maximumCommand), "cal 5 %04u 3000\n",
      ServoBench::CalibrationMaximumUs);
  Send(controller, maximumCommand, 20000001);
  assert(!outputs.enabled && outputs.pulses == 1); // Busy cancels and never queues.
  Send(controller, maximumCommand, 20000002);
  assert(outputs.pulses == 1);
}

static void CheckHelpReportsConfiguredLimits() {
  FakeOutputs outputs;
  ServoBench::Controller controller(outputs);
  Send(controller, "help\n");
  char expected[160];
  std::snprintf(expected, sizeof(expected),
      "arm | pulse <channel 0-5> <us 1400-1600> | cal <channel 0-5> <us %04u-%04u> "
      "<ms 0500-3000> | stop | ! | help",
      ServoBench::CalibrationMinimumUs, ServoBench::CalibrationMaximumUs);
  assert(outputs.lastReport == expected);
}

static void CheckCalibrationGrammar() {
  const char* invalid[] = {
    "cal 6 1500 0500\n", "cal 16 1500 0500\n", "cal -1 1500 0500\n",
    "cal 0 0499 0500\n", "cal 0 2401 0500\n", "cal 0 1500 0499\n",
    "cal 0 1500 3001\n", "cal 0 1500 500\n", "cal 0 1500 03000\n",
    "cal 0 1500 +500\n", "cal 0 1500 5e02\n", "cal 0 1e03 0500\n",
    "cal 0 +500 0500\n", "cal 0 10000 0500\n", "cal 0 1500 0500 extra\n",
    "cal 0 1500 0500 \n", " cal 0 1500 0500\n", "cal  0 1500 0500\n",
    "cal 0 1500\t0500\n", "cal 0 1500 05\t0\n", "CAL 0 1500 0500\n"
  };
  for (const char* command : invalid) {
    FakeOutputs outputs;
    ServoBench::Controller controller(outputs);
    Send(controller, "arm\n");
    Send(controller, command, 100);
    Send(controller, "cal 0 1500 0500\n", 101);
    assert(!outputs.enabled && outputs.pulses == 0); // Every rejection disarms.
  }
}

static void CheckCalibrationStopAndFault() {
  char minimumCommand[32], maximumCommand[32];
  std::snprintf(minimumCommand, sizeof(minimumCommand), "cal 0 %04u 0500\n",
      ServoBench::CalibrationMinimumUs);
  std::snprintf(maximumCommand, sizeof(maximumCommand), "cal 5 %04u 3000\n",
      ServoBench::CalibrationMaximumUs);
  for (const char* command : {minimumCommand, maximumCommand}) {
    RegisterOutputs outputs;
    ServoBench::Controller controller(outputs);
    Send(controller, "arm\n");
    Send(controller, command, 100);
    const unsigned selected = static_cast<unsigned>(command[4] - '0');
    const unsigned ticks = ServoBench::PulseTicks(
        selected == 0 ? ServoBench::CalibrationMinimumUs : ServoBench::CalibrationMaximumUs);
    ServoBench::PwmSnapshot snapshot;
    assert(ServoBench::ReadPwmSnapshot(outputs.bus, selected, snapshot));
    assert(snapshot.Matches(ticks) && !snapshot.FullOff() && !snapshot.FullOn());
    for (unsigned channel = 0; channel < 16; ++channel) {
      if (channel != selected) assert((outputs.bus.registers[0x09 + channel * 4] & 0x10) != 0);
    }
    Send(controller, "!", 200);
    AssertAllOff(outputs.bus);
  }
  for (const char* stop : {"stop\n", "partial!", "cal 0 1500 500\n", "timeout"}) {
    RegisterOutputs outputs;
    ServoBench::Controller controller(outputs);
    char command[32];
    std::snprintf(command, sizeof(command), "arm\ncal 5 %04u 3000\n",
        ServoBench::CalibrationMaximumUs);
    Send(controller, command, 100);
    ServoBench::PwmSnapshot snapshot;
    assert(ServoBench::ReadPwmSnapshot(outputs.bus, 5, snapshot));
    assert(snapshot.Matches(ServoBench::PulseTicks(ServoBench::CalibrationMaximumUs)));
    if (std::strcmp(stop, "timeout") == 0) {
      controller.Tick(3000099);
      assert((outputs.bus.registers[0x1d] & 0x10) == 0);
      controller.Tick(3000100);
    } else {
      Send(controller, stop, 200);
    }
    AssertAllOff(outputs.bus);
    assert(!controller.Faulted());
    Send(controller, "\ncal 0 1000 0500\n", 3000200);
    AssertAllOff(outputs.bus); // Stop and rejected busy input leave no arm.
  }
  for (unsigned failedRead = 1; failedRead <= 7; ++failedRead) {
    RegisterOutputs outputs;
    ServoBench::Controller controller(outputs);
    Send(controller, "arm\n");
    outputs.bus.failAt = outputs.bus.operations + 3 + failedRead;
    Send(controller, "cal 0 1000 0500\n", 100);
    assert(controller.Faulted());
    AssertAllOff(outputs.bus);
    Send(controller, "arm\ncal 0 1000 0500\n", 200);
    AssertAllOff(outputs.bus);
  }
  {
    RegisterOutputs outputs;
    ServoBench::Controller controller(outputs);
    Send(controller, "arm\n");
    outputs.bus.corruptReadback = true;
    char command[24];
    std::snprintf(command, sizeof(command), "cal 5 %04u 3000\n",
        ServoBench::CalibrationMaximumUs);
    Send(controller, command, 100);
    assert(controller.Faulted());
    AssertAllOff(outputs.bus);
  }
  for (bool timeout : {true, false}) {
    RegisterOutputs outputs;
    ServoBench::Controller controller(outputs);
    Send(controller, "arm\ncal 0 1000 0500\n", 100);
    outputs.bus.failAt = outputs.bus.operations + 1;
    if (timeout) controller.Tick(500100);
    else Send(controller, "!", 200);
    assert(controller.Faulted());
    assert(outputs.lastReport.find("PWM_OFF_FAILED") != std::string::npos);
    Send(controller, "\nstop\narm\ncal 0 1000 0500\n", 500200);
    AssertAllOff(outputs.bus); // Successful stop retries do not clear the fault.
  }
  for (bool preparationFailure : {true, false}) {
    FakeOutputs outputs;
    outputs.failPrepare = preparationFailure;
    outputs.failPulse = !preparationFailure;
    ServoBench::Controller controller(outputs);
    Send(controller, "arm\ncal 0 1000 0500\n");
    assert(controller.Faulted() && !outputs.enabled);
    const unsigned attempts = outputs.pulses;
    outputs.failPrepare = outputs.failPulse = false;
    Send(controller, "arm\ncal 0 1000 0500\n");
    assert(outputs.pulses == attempts && !outputs.enabled);
  }
}

int main() {
  CheckRegisters();
  CheckReadback();
  CheckPcaStop();
  CheckVerifiedPulseFailure();
  CheckCalibrationTiming();
  CheckHelpReportsConfiguredLimits();
  CheckCalibrationGrammar();
  CheckCalibrationStopAndFault();
  {
    FakeOutputs outputs;
    ServoBench::Controller controller(outputs);
    assert(!outputs.enabled);
    Send(controller, "pulse 0 1500\n");
    assert(outputs.pulses == 0);
    Send(controller, "arm\r\npulse 0 1500\r\n", 100);
    assert(outputs.enabled && outputs.pulses == 1 && outputs.channel == 0);
    assert(outputs.ticks == 307);
    assert(outputs.lastReport == "PULSE nominal 500 ms; timeout/Stop writes PCA FULL_OFF");
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
    "pulse 0 1399\n", "pulse 0 1601\n", "pulse 0 1000\n", "pulse 0 2000\n",
    "pulse 0 1500 extra\n",
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

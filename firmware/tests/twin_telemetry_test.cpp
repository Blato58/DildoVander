#include "physical_effects_test_support.h"
#include "../src/features/twin_telemetry/TwinTelemetry.h"
#include <chrono>
#include <iostream>
#include <memory>

int main(int argc, char**) {
  FakeIo driver;
  TwinObservedIo io(driver);
  PhysicalEffects effects(io);
  std::array<ServoCalibration, 6> calibration{};
  calibration.fill({1100, 1900, 500});
  assert(effects.Configure(calibration, 1));
  effects.Tick(0);
  assert(effects.RecoverAfterInspection());
  assert(effects.Move(3, true));
  assert(io.pulse_[2] == 1900 && io.enabled_);
  effects.Tick(100);
  effects.Fault();
  assert(!io.enabled_ && !effects.IsResting(3) && !effects.IsRaised(3));
  // FullOff disables PWM, retaining only the last accepted pulse command.
  assert(io.pulse_[2] == 1900);
  driver.fail = true;
  assert(!io.Servo(2, 1100));
  assert(io.pulse_[2] == 1900);
  driver.fail = false;
  assert(io.Lights(std::array<uint8_t, 108>{}));
  TwinTelemetryMailbox mailbox;
  std::unique_ptr<TwinTelemetrySnapshot> input(new TwinTelemetrySnapshot);
  std::unique_ptr<TwinTelemetrySnapshot> output(new TwinTelemetrySnapshot);
  const auto start = std::chrono::steady_clock::now();
  for (uint64_t sequence = 1; sequence <= 100000; ++sequence) {
    input->sequence = sequence;
    assert(mailbox.Publish(*input));
  }
  const auto micros = std::chrono::duration_cast<std::chrono::microseconds>(
      std::chrono::steady_clock::now() - start).count();
  assert(mailbox.Take(*output) && output->sequence == 100000);
  assert(!mailbox.Take(*output) && mailbox.Dropped() == 99999);
  output->pulse = io.pulse_;
  output->trace.bootId = "synthetic-live-producer";
  const auto json = ExportTwinTelemetry(*output, "synthetic-live", "synthetic-live-producer",
      "test-build", "test-build", mailbox.Dropped());
  assert(json.find("\"physicalVerification\":\"unavailable\"") != std::string::npos);
  assert(json.find("\"model\":null") != std::string::npos);
  assert(json.find("\"calibrationRevision\":null") != std::string::npos);
  assert(json.find("\"pulseUs\":1900,\"estimate\":\"unknown\"") != std::string::npos);
  assert(json.find("credential") == std::string::npos);
  if (argc > 1) std::cout << json << '\n';
  else std::cout << "PASS telemetry bounded latest mailbox: 100000 publications, " << micros
                << " us total, coalesced=99999; Stop preserves unknown estimates\n";
}

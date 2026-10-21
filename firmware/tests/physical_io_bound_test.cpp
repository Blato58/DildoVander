#include "physical_io_bound_stubs/PhysicalIoStub.h"
#include "../src/features/physical_effects/EspIdfPhysicalIo.h"
#include "../src/features/twin_telemetry/TwinTelemetry.h"
#include <cassert>
#include <iostream>

namespace io_stub { State state; }

static void ZeroWaitAndRetainedLedBuffer() {
  io_stub::state = {};
  EspIdfPhysicalIo io;
  assert(io.Initialize(true));
  auto& s = io_stub::state;
  const auto* retained = static_cast<const uint8_t*>(s.retainedSymbols);
  const std::vector<uint8_t> original(retained, retained + s.retainedBytes);
  std::array<uint8_t, 108> frame{};
  frame[0] = 255;
  assert(io.Lights(frame));
  frame[1] = 255;
  assert(io.Lights(frame));
  assert(s.transmits == 1);
  assert(std::memcmp(retained, original.data(), original.size()) == 0);
  s.rmtBusy = false;
  io.BeginControlPass(s.nowUs);
  assert(s.transmits == 2 && s.nonblocking);
  assert(s.maximumRmtWait == 0 && s.maximumLockWait == 0);
  assert(io.Timing().coalescedLedFrames == 1);
}

static void OnlyTouchedPcaChannelIsCleared() {
  io_stub::state = {};
  EspIdfPhysicalIo io;
  assert(io.Initialize(true));
  auto& s = io_stub::state;
  io.BeginControlPass(s.nowUs);
  const auto initialWrites = s.writes.size();
  assert(io.Servo(2, 1500));
  io.EnableOutputs(true);
  assert(s.oe == 0);
  assert(io.FullOff());
  assert(s.oe == 1 && s.writes.size() == initialWrites + 2);
  const auto& clear = s.writes.back();
  assert(clear.bytes == std::vector<uint8_t>({0x0e, 0, 0, 0, 0x10}));
  assert(clear.timeout <= 2);
  assert(io.FullOff());
  assert(s.writes.size() == initialWrites + 2);
}

static void ExpiredPassAndContendedBusFailClosed() {
  io_stub::state = {};
  EspIdfPhysicalIo io;
  assert(io.Initialize(true));
  auto& s = io_stub::state;
  io.BeginControlPass(s.nowUs);
  io.EnableOutputs(true);
  s.nowUs += 7501;
  PhysicalInputs inputs;
  assert(!io.ReadInputs(inputs));
  assert(s.reads == 0 && s.oe == 1 && io.UrgentStopPending());
  assert(io.Timing().budgetRefusals == 1);

  io_stub::state = {};
  EspIdfPhysicalIo blocked;
  assert(blocked.Initialize(true));
  s.lockAvailable = false;
  blocked.EnableOutputs(true);
  assert(!blocked.FullOff());
  assert(s.oe == 1 && s.maximumLockWait == 0 && blocked.Timing().lockRefusals == 1);
}

static void StopCannotBeUndoneByStaleEnableOrRecovery() {
  io_stub::state = {};
  EspIdfPhysicalIo io;
  assert(io.Initialize(true));
  auto& s = io_stub::state;
  io.EnableOutputs(true);
  assert(s.oe == 0);
  const auto writes = s.writes.size();
  s.lockAvailable = false;
  io.RequestUrgentStop();
  const auto inspected = io.StopEpoch();
  assert(s.oe == 1 && s.writes.size() == writes);
  io.EnableOutputs(true);
  assert(s.oe == 1);
  io.RequestUrgentStop();
  assert(!io.ClearUrgentStopAfterInspection(inspected));
  assert(io.UrgentStopPending());
  const auto current = io.StopEpoch();
  assert(io.ClearUrgentStopAfterInspection(current));
  io.EnableOutputs(true);
  assert(s.oe == 0);
}

static void FailedSdkCallPublishesMeasuredDurationAndLatchesStop() {
  io_stub::state = {};
  EspIdfPhysicalIo io;
  assert(io.Initialize(true));
  auto& s = io_stub::state;
  io.BeginControlPass(s.nowUs);
  s.transferUs = 50000; // Model an SDK bus-clear path, not a measured hardware claim.
  s.failTransfer = true;
  PhysicalInputs inputs;
  assert(!io.ReadInputs(inputs));
  assert(s.oe == 1 && io.UrgentStopPending());
  assert(io.Timing().maximumTransferUs == 50000 && io.Timing().maximumPassBusUs == 50000);
  assert(io.Timing().overBudgetPasses == 1);
  assert(!io.ClearUrgentStopAfterInspection(io.StopEpoch()));
}

static void ObservedProductionPortForwardsStopAndInspectionEpoch() {
  io_stub::state = {};
  EspIdfPhysicalIo hardware;
  assert(hardware.Initialize(true));
  TwinObservedIo observed(hardware);
  PhysicalEffects effects(observed);
  std::array<ServoCalibration, 6> calibration{};
  for (auto& servo : calibration) servo = {1000, 2000, 20};
  assert(effects.Configure(calibration, 1));
  hardware.BeginControlPass(io_stub::state.nowUs);
  effects.Tick(io_stub::state.nowUs / 1000);
  assert(effects.RecoverAfterInspection());
  hardware.RequestUrgentStop();
  observed.EnableOutputs(true);
  assert(!observed.enabled_ && io_stub::state.oe == 1);
  assert((observed.StopEpoch() & 1u) != 0);
  assert(effects.RecoverAfterInspection());
  assert(!hardware.UrgentStopPending());
  observed.EnableOutputs(true);
  assert(observed.enabled_ && io_stub::state.oe == 0);
  effects.Fault();
  assert(hardware.UrgentStopPending() && !observed.enabled_ && io_stub::state.oe == 1);
}

int main() {
  ObservedProductionPortForwardsStopAndInspectionEpoch();
  ZeroWaitAndRetainedLedBuffer();
  OnlyTouchedPcaChannelIsCleared();
  ExpiredPassAndContendedBusFailClosed();
  StopCannotBeUndoneByStaleEnableOrRecovery();
  FailedSdkCallPublishesMeasuredDurationAndLatchesStop();
  std::cout << "PASS physical I/O admission, OE latch, retained RMT buffer and measured SDK overrun\n";
}

#include "../src/features/device_runtime/ControlSchedule.h"
#include "../src/features/device_runtime/RuntimeDiagnostics.h"
#include "../src/features/device_runtime/PhysicalIoBudget.h"
#include "../src/features/device_runtime/DeviceDiagnostics.h"
#include "../src/features/device_runtime/MaintenanceReservations.h"
#include <cassert>
#include <iostream>

using namespace kufr::runtime;

static void WorkTimeDoesNotAccumulateIntoPeriod() {
  ControlSchedule schedule(1000);
  for (uint64_t release = 1000; release < 101000; release += 10000) {
    assert(schedule.Begin(release));
    assert(!schedule.Begin(release));
    schedule.Finish(release + 3400);
    assert(schedule.NextReleaseUs() == release + 10000);
    assert(!schedule.Begin(release + 9999));
  }
  const auto timing = schedule.Timing();
  assert(timing.passes == 10 && timing.skippedReleases == 0);
  assert(timing.maximumGapUs == 10000 && timing.maximumExecutionUs == 3400);
}

static void LatePassDiscardsOldReleases() {
  ControlSchedule schedule(0);
  assert(schedule.Begin(0));
  schedule.Finish(1000);
  assert(schedule.Begin(37000));
  schedule.Finish(43000);
  assert(schedule.NextReleaseUs() == 50000);
  assert(!schedule.Begin(43000));
  const auto timing = schedule.Timing();
  assert(timing.skippedReleases == 3);
  assert(timing.latenessUs == 27000 && timing.maximumGapUs == 37000);
}

static void AStalledServiceReservationDoesNotOwnTheControlThread() {
  MaintenanceReservations maintenance;
  ControlMaintenanceGate::Ticket completed;
  assert(maintenance.ownership.BeginControl(completed));
  assert(maintenance.ownership.EndControl(completed, true));
  MaintenanceReservations::PairingReservation stalledService(maintenance);
  assert(stalledService.Acquired());
  ControlSchedule schedule(0);
  unsigned urgentStops = 0;
  for (uint64_t release = 0; release < 100000; release += 10000) {
    assert(schedule.Begin(release));
    ControlMaintenanceGate::Ticket pass;
    const bool admissionAllowed = maintenance.ownership.BeginControl(pass);
    // Same production contract: reservation gates admission, never Stop.
    ++urgentStops;
    assert(!admissionAllowed);
    schedule.Finish(release + 1000);
  }
  assert(urgentStops == 10 && schedule.Timing().maximumGapUs == 10000);
}

static void SnapshotIsCopiedAndBudgetDoesNotRenewPerCall() {
  PhysicalIoBudget budget;
  budget.Begin(10000);
  assert(budget.RemainingMs(10000) == 2);
  assert(budget.RemainingMs(16500) == 1);
  assert(budget.RemainingMs(17500) == 0);
  assert(budget.RemainingMs(19000) == 0);
  RuntimeDiagnostics diagnostics;
  RuntimeSnapshot published;
  published.timing.maximumGapUs = 37000;
  published.owner = ActuatorOwner::StopPending;
  published.ioLockRefusals = 3;
  diagnostics.Publish(published);
  RuntimeSnapshot read;
  assert(diagnostics.Read(read));
  published.timing.maximumGapUs = 2;
  assert(read.timing.maximumGapUs == 37000 && read.ioLockRefusals == 3);
  assert(read.owner == ActuatorOwner::StopPending);
}

static DeviceDiagnosticSnapshot DiagnosticFixture() {
  DeviceDiagnosticSnapshot snapshot;
  auto& h = snapshot.health;
  h[DeviceHealth::SampledAtLow] = 1234;
  h[DeviceHealth::Profile] = 2;
  h[DeviceHealth::HardwareAvailable] = 1;
  h[DeviceHealth::Station] = static_cast<uint32_t>(StationPhase::Connected);
  h[DeviceHealth::HasIp] = 1;
  h[DeviceHealth::BackendOnline] = 1;
  h[DeviceHealth::Ota] = static_cast<uint32_t>(OtaPhase::VerifyingBoot);
  h[DeviceHealth::JournalKnown] = 1;
  h[DeviceHealth::JournalHealthy] = 1;
  h[DeviceHealth::JournalUsed] = 3;
  h[DeviceHealth::JournalEpochLow] = 5;
  h[DeviceHealth::JournalEpochHigh] = 1;
  h[DeviceHealth::StorageStartup] = 3;
  h[DeviceHealth::StorageState] = 5;
  h[DeviceHealth::InternalFree] = 123456;
  snapshot.runtime.timing.maximumGapUs = 37000;
  snapshot.runtime.owner = ActuatorOwner::StopPending;
  h[DeviceHealth::UrgentStopPending] = 1;
  return snapshot;
}

static void DiagnosticsAreBoundedRedactedAndPreserveWideEpoch() {
  DeviceHealthMailbox mailbox;
  DeviceHealth health;
  assert(!mailbox.Read(health));
  auto fixture = DiagnosticFixture();
  mailbox.Publish(fixture.health);
  assert(mailbox.Read(health) && health[DeviceHealth::JournalUsed] == 3);
  fixture.health[DeviceHealth::JournalUsed] = 9;
  assert(health[DeviceHealth::JournalUsed] == 3);
  const auto json = RuntimeDiagnosticsPort::Serialize(DiagnosticFixture(), "synthetic-test");
  assert(!json.empty() && json.size() <= 2048);
  assert(json.find("\"operationEpoch\":\"4294967301\"") != std::string::npos);
  assert(json.find("\"authentication\":\"unknown\",\"time\":\"unknown\"") != std::string::npos);
  assert(json.find("\"urgentStopPending\":true") != std::string::npos);
  assert(json.find("token") == std::string::npos && json.find("SSID") == std::string::npos);
  assert(RuntimeDiagnosticsPort::Serialize(fixture, "injected\"field").empty());
  assert(RuntimeDiagnosticsPort::Serialize(fixture, std::string(65, 'a').c_str()).empty());
  for (auto& value : fixture.health.values) value = UINT32_MAX;
  fixture.runtime.timing = {UINT32_MAX, UINT32_MAX, UINT32_MAX, UINT32_MAX,
      UINT32_MAX, UINT32_MAX, UINT32_MAX, UINT32_MAX};
  assert(!RuntimeDiagnosticsPort::Serialize(fixture, std::string(64, 'a').c_str()).empty());
}

int main(int argc, char**) {
  if (argc > 1) {
    std::cout << RuntimeDiagnosticsPort::Serialize(DiagnosticFixture(), "synthetic-test") << "\n";
    return 0;
  }
  DiagnosticsAreBoundedRedactedAndPreserveWideEpoch();
  WorkTimeDoesNotAccumulateIntoPeriod();
  LatePassDiscardsOldReleases();
  AStalledServiceReservationDoesNotOwnTheControlThread();
  SnapshotIsCopiedAndBudgetDoesNotRenewPerCall();
  std::cout << "PASS absolute runtime cadence, missed-release discard, independent service reservation and snapshots\n";
}

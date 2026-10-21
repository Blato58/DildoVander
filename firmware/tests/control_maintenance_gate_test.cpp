#include <atomic>
#include <cassert>
#include <condition_variable>
#include <mutex>
#include <thread>
#include <type_traits>

#include "../src/features/device_control/ControlMaintenanceGate.h"

using Gate = ControlMaintenanceGate;
static_assert(!std::is_copy_constructible<Gate::Ticket>::value, "maintenance authority is single owner");

void PublicationAndHandoff() {
  Gate gate;
  Gate::Ticket control, network, ota;
  assert(!gate.Acquire(Gate::Owner::Network, network));
  assert(gate.BeginControl(control));
  assert(!gate.Acquire(Gate::Owner::Network, network));
  assert(gate.EndControl(control, true));
  assert(!gate.EndControl(control, true));
  assert(gate.Acquire(Gate::Owner::Network, network));
  Gate otherGate;
  Gate::Ticket otherControl, otherNetwork;
  assert(otherGate.BeginControl(otherControl) && otherGate.EndControl(otherControl, true));
  assert(otherGate.Acquire(Gate::Owner::Network, otherNetwork));
  assert(!otherGate.Release(Gate::Owner::Network, network));
  assert(otherNetwork.Active() && network.Active());
  assert(otherGate.Release(Gate::Owner::Network, otherNetwork));
  assert(!gate.BeginControl(control));
  assert(!gate.Release(Gate::Owner::Ota, network));
  assert(network.Active());
  assert(gate.Release(Gate::Owner::Network, network));
  assert(!gate.Acquire(Gate::Owner::Ota, ota));
  assert(gate.BeginControl(control));
  assert(gate.TransferControl(control, Gate::Owner::Ota, ota));
  assert(!gate.EndControl(control, true));
  assert(gate.HeldBy() == Gate::Owner::Ota);
  assert(!gate.BeginControl(control));
  assert(gate.Release(Gate::Owner::Ota, ota));
  assert(gate.BeginControl(control));
  assert(gate.EndControl(control, false));
  assert(!gate.Acquire(Gate::Owner::Network, network));
}

void StalledMaintenanceDoesNotOwnTheControlThread() {
  Gate gate;
  Gate::Ticket first;
  assert(gate.BeginControl(first) && gate.EndControl(first, true));
  std::mutex mutex;
  std::condition_variable changed;
  bool entered = false, release = false;
  std::thread worker([&] {
    Gate::Ticket pairing;
    assert(gate.Acquire(Gate::Owner::Pairing, pairing));
    std::unique_lock<std::mutex> lock(mutex);
    entered = true;
    changed.notify_one();
    changed.wait(lock, [&] { return release; });
    assert(gate.Release(Gate::Owner::Pairing, pairing));
  });
  {
    std::unique_lock<std::mutex> lock(mutex);
    changed.wait(lock, [&] { return entered; });
  }
  unsigned stops = 0;
  for (unsigned tick = 0; tick != 1000; ++tick) {
    Gate::Ticket pass;
    assert(!gate.BeginControl(pass));
    assert(gate.HeldBy() == Gate::Owner::Pairing);
    // The production caller still performs urgent Stop/output/status work on
    // this path, suppressing admissions rather than waiting for maintenance.
    ++stops;
  }
  assert(stops == 1000);
  {
    std::lock_guard<std::mutex> lock(mutex);
    release = true;
  }
  changed.notify_one();
  worker.join();
  assert(gate.BeginControl(first) && gate.EndControl(first, true));
}

void ConcurrentAdmissionIsExclusive() {
  Gate gate;
  Gate::Ticket pass;
  assert(gate.BeginControl(pass) && gate.EndControl(pass, true));
  std::atomic<unsigned> inside{0};
  std::thread worker([&] {
    for (unsigned attempt = 0; attempt != 20000; ++attempt) {
      Gate::Ticket network;
      if (!gate.Acquire(Gate::Owner::Network, network)) continue;
      assert(inside.fetch_add(1) == 0);
      assert(inside.fetch_sub(1) == 1);
      assert(gate.Release(Gate::Owner::Network, network));
    }
  });
  for (unsigned attempt = 0; attempt != 20000; ++attempt) {
    Gate::Ticket control;
    if (!gate.BeginControl(control)) continue;
    assert(inside.fetch_add(1) == 0);
    assert(inside.fetch_sub(1) == 1);
    assert(gate.EndControl(control, true));
  }
  worker.join();
}

int main() {
  PublicationAndHandoff();
  StalledMaintenanceDoesNotOwnTheControlThread();
  ConcurrentAdmissionIsExclusive();
}

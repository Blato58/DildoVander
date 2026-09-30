#include <cassert>
#include <string>

#include "../src/features/device_control/DeviceControlRuntime.h"
#include "../src/features/local_control/LocalControlSession.h"
#include "../src/features/safety/SafetyController.h"

namespace {

constexpr char BootId[] = "ac165d39-9f2a-4f3e-a613-3ad676a39d2e";
constexpr char OtherBootId[] = "12f0b2f9-27cc-4f19-b28e-0bd18855961b";
constexpr char LeaseId[] = "287d9ff1-c62b-43b9-8f36-f92de0ae383e";
constexpr char OtherLeaseId[] = "95720438-72b5-4533-bc80-319c6d78181a";

class FakeSafetyPort final : public SafetyPort {
 public:
  void DisableActuators() override {}
  void StartVibration(uint8_t) override {}
  void ReleaseLatch(uint8_t) override {}
};

class FakeJournal final : public CommandJournal {
 public:
  bool HasPending() const override { return false; }
  uint8_t PendingCompartment() const override { return 0; }
  uint8_t ReadyMask() const override { return 63; }
  bool Begin(const std::string&, uint8_t) override { return false; }
  bool Resolve(uint8_t) override { return false; }
  bool PersistReadyMask(uint8_t) override { return false; }
};

struct Fixture {
  Fixture()
      : safety(port),
        sequence(safety),
        control("kufr-1", BootId, sequence, journal, 1),
        state(false, 0, ""),
        runtime(control, sequence, state, "neutral-1") {
    safety.InitializeSafe();
    assert(runtime.RecoverAfterInspection());
  }

  FakeSafetyPort port;
  SafetyController safety;
  SelectionSequence sequence;
  FakeJournal journal;
  DeviceControl control;
  ConversationRuntimeState state;
  DeviceControlRuntime runtime;
};

void TestObserverDisconnectDoesNotChangeControl() {
  Fixture fixture;
  const uint64_t generation = fixture.runtime.Generation();
  const uint64_t revision = fixture.control.Revision();
  LocalControlSession observer(fixture.runtime, "kufr-1", BootId, 100);
  assert(observer.Hello(1, "kufr-1", 200));
  SelectionStep step;
  observer.Disconnect(step);
  observer.Disconnect(step);
  assert(!fixture.control.HasManualControl());
  assert(fixture.runtime.Generation() == generation);
  assert(fixture.control.Revision() == revision);
  assert(!observer.Acquire(BootId, LeaseId, 300));
}

void TestHelloAndLeaseGuards() {
  Fixture fixture;
  LocalControlSession session(fixture.runtime, "kufr-1", BootId, 100);
  assert(!session.Acquire(BootId, LeaseId, 100));
  assert(!session.Hello(2, "kufr-1", 101));
  assert(!session.Hello(1, "other-device", 101));
  assert(!session.Hello(1, "kufr-1", 10101));
  assert(session.Hello(1, "kufr-1", 10100));
  assert(!session.Hello(1, "kufr-1", 10100));
  assert(!session.Acquire(OtherBootId, LeaseId, 10100));
  assert(!session.Acquire(BootId, "invalid", 10100));
  assert(session.Acquire(BootId, LeaseId, 10100));
  assert(fixture.control.HasManualControl());
  assert(!session.Acquire(BootId, OtherLeaseId, 10101));
  assert(!session.Heartbeat(BootId, OtherLeaseId, 10101));
  assert(!session.Heartbeat(OtherBootId, LeaseId, 10101));
  assert(session.Heartbeat(BootId, LeaseId, 10101));
  SelectionStep step;
  assert(!session.Release(BootId, OtherLeaseId, step));
  assert(session.Release(BootId, LeaseId, step));
  assert(!fixture.control.HasManualControl());
  assert(fixture.runtime.Generation() == 1);
}

void TestExpiryAndOwnerDisconnect() {
  Fixture fixture;
  LocalControlSession session(fixture.runtime, "kufr-1", BootId, 0);
  assert(session.Hello(1, "kufr-1", 0));
  assert(session.Acquire(BootId, LeaseId, 0));
  assert(!session.Heartbeat(BootId, LeaseId, 6000));
  assert(!fixture.control.HasManualControl());
  assert(fixture.runtime.Generation() == 1);
  SelectionStep step;
  session.Disconnect(step);
  assert(fixture.runtime.Generation() == 1);

  LocalControlSession next(fixture.runtime, "kufr-1", BootId, 7000);
  assert(next.Hello(1, "kufr-1", 7000));
  assert(!next.Acquire(BootId, OtherLeaseId, 7000));
  assert(fixture.runtime.RecoverAfterInspection());
  assert(next.Acquire(BootId, OtherLeaseId, 7000));
  next.Disconnect(step);
  assert(!fixture.control.HasManualControl());
  assert(fixture.runtime.Generation() == 2);
}

void TestExpiredSessionCannotRevokeNextOwner() {
  Fixture fixture;
  LocalControlSession oldSession(fixture.runtime, "kufr-1", BootId, 0);
  assert(oldSession.Hello(1, "kufr-1", 0));
  assert(oldSession.Acquire(BootId, LeaseId, 0));
  SelectionStep step;
  fixture.runtime.Tick(6000, step);
  assert(!fixture.control.HasManualControl());
  assert(fixture.runtime.Generation() == 1);
  assert(fixture.runtime.RecoverAfterInspection());

  LocalControlSession nextSession(fixture.runtime, "kufr-1", BootId, 7000);
  assert(nextSession.Hello(1, "kufr-1", 7000));
  assert(nextSession.Acquire(BootId, OtherLeaseId, 7000));
  assert(!oldSession.Release(BootId, LeaseId, step));
  oldSession.Disconnect(step);
  assert(fixture.control.HasManualControl());
  assert(fixture.runtime.Generation() == 1);
  assert(nextSession.Heartbeat(BootId, OtherLeaseId, 7001));
  assert(nextSession.Release(BootId, OtherLeaseId, step));
  assert(fixture.runtime.Generation() == 2);
}

}  // namespace

int main() {
  TestObserverDisconnectDoesNotChangeControl();
  TestHelloAndLeaseGuards();
  TestExpiryAndOwnerDisconnect();
  TestExpiredSessionCannotRevokeNextOwner();
}

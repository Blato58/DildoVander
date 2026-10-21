#include <algorithm>
#include <cassert>
#include <cstdio>
#include <vector>

#include "../src/features/device_control/DeviceStorageStartup.h"

namespace {
class Storage final : public JournalStorage {
 public:
  JournalReadResult Read(uint8_t* output, size_t capacity, size_t& length) override {
    assert(worker);
    length = bytes.size();
    if (failRead || length > capacity) return JournalReadResult::Failed;
    if (bytes.empty()) return JournalReadResult::Missing;
    std::copy(bytes.begin(), bytes.end(), output);
    return JournalReadResult::Found;
  }
  bool Write(const uint8_t* input, size_t length) override {
    assert(worker);
    ++writes;
    bytes.assign(input, input + length);
    return true;
  }
  std::vector<uint8_t> bytes;
  unsigned writes = 0;
  bool worker = true, failRead = false;
};
JournalIdentityView Identity(bool provisioned = true) {
  JournalIdentityView result;
  result.healthy = true;
  result.provisioned = provisioned;
  if (provisioned) result.deviceId = "device-001";
  return result;
}
std::string Id(unsigned number) {
  char id[37];
  std::snprintf(id, sizeof(id), "aaaaaaaa-aaaa-4aaa-8aaa-%012x", number);
  return id;
}
struct Fixture {
  void Establish() {
    PersistentCommandJournal journal(a);
    JournalIdentityOwner marker(b, journal);
    KeepsakeCommittedInventory online(c);
    kufr::offline::OfflineCommittedInventory offline(d);
    assert(marker.BeginFactory("device-001", Identity(false), true));
    assert(journal.EstablishScopedIdentity("device-001", true));
    assert(online.Establish("device-001", true));
    assert(offline.EstablishBinding("device-001", true));
    JournalEvidenceInventory one, two;
    assert(online.ReadCommitted(one) && offline.ReadCommitted(two));
    assert(marker.Finalize(Identity(), one, two, true));
    assert(journal.PersistReadyMask(63));
  }
  void Worker(bool enabled) { a.worker = b.worker = c.worker = d.worker = enabled; }
  unsigned Writes() const { return a.writes + b.writes + c.writes + d.writes; }
  Storage a, b, c, d;
};

void MissingDoesNotCreateAnEmptyJournal() {
  Fixture fixture;
  DeviceStorageStartup startup(fixture.a, fixture.b, fixture.c, fixture.d, Identity());
  assert(!startup.Boot());
  assert(startup.RequiresService() && !startup.ImageStorageHealthy() && !startup.Runtime());
  fixture.Worker(false);
  auto& control = startup.ControlJournal();
  assert(control.HasPending() && control.HasStorageFault());
  assert(!control.Begin(Id(1), 1) && !control.Resolve(63) && !control.PersistReadyMask(63));
  assert(!startup.RunOne() && !startup.Boot() && fixture.Writes() == 0);
}

void FoundUnscopedOriginMissingIsRetainedForExplicitService() {
  Fixture fixture;
  {
    PersistentCommandJournal original(fixture.a);
    assert(original.PersistReadyMask(63));
    assert(original.BeginWithIdentity(Id(1), 1, 99));
    assert(original.ResolveRestored(1));
    assert(original.Origin() == PersistentCommandJournal::StateOrigin::Missing);
    assert(!original.IsStorageMissing());
  }
  const auto bytes = fixture.a.bytes;
  const unsigned writes = fixture.Writes();
  DeviceStorageStartup startup(fixture.a, fixture.b, fixture.c, fixture.d, Identity());
  assert(!startup.Boot() && startup.CurrentState() == DeviceStorageStartup::State::ServiceRequired);
  assert(startup.ImageStorageHealthy() && startup.RequiresService());
  fixture.Worker(false);
  assert(startup.ControlJournal().ReadyMask() == 63 && startup.ControlJournal().HasStorageFault());
  assert(!startup.ControlJournal().Begin(Id(2), 1));
  assert(fixture.a.bytes == bytes && fixture.Writes() == writes && fixture.b.bytes.empty());
}

void EstablishedBootAdoptsTheReconciledEpochBeforeControl() {
  Fixture fixture;
  fixture.Establish();
  {
    PersistentCommandJournal original(fixture.a);
    for (unsigned operation = 1; operation <= 32; ++operation) {
      assert(original.BeginScoped(1, Id(operation), 1, 100 + operation));
      assert(original.ResolveRestored(1));
    }
    assert(original.CurrentEpoch() == 1);
  }
  const unsigned journalWrites = fixture.a.writes;
  DeviceStorageStartup startup(fixture.a, fixture.b, fixture.c, fixture.d, Identity(),
      DeviceStorageRuntime::Policy::BoundedCompaction);
  assert(startup.Boot() && startup.Runtime() && startup.ImageStorageHealthy());
  assert(startup.CurrentState() == DeviceStorageStartup::State::Established && !startup.RequiresService());
  fixture.Worker(false);
  assert(startup.ControlJournal().CurrentEpoch() == 2);
  assert(startup.ControlJournal().ReadyMask() == 63);
  assert(startup.ControlJournal().FindHistorical(1, Id(1), 1, 101) == CommandJournal::Lookup::Missing);
  assert(fixture.a.writes == journalWrites + 1);
}

void PendingInspectionIsNotImageFailureOrBootResolution() {
  Fixture fixture;
  fixture.Establish();
  {
    PersistentCommandJournal journal(fixture.a);
    assert(journal.BeginScoped(1, Id(1), 2, 111));
  }
  const unsigned writes = fixture.Writes();
  DeviceStorageStartup startup(fixture.a, fixture.b, fixture.c, fixture.d, Identity());
  assert(startup.Boot() && startup.ImageStorageHealthy() && !startup.RequiresService());
  assert(startup.Runtime()->CurrentState() == DeviceStorageRuntime::State::InspectionRequired);
  fixture.Worker(false);
  assert(startup.ControlJournal().HasPending() && startup.ControlJournal().PendingCompartment() == 2);
  assert(!startup.Runtime()->AllowsAdmission() && fixture.Writes() == writes);
}

void EveryLostOrCorruptEstablishedStoreFailsClosed() {
  for (unsigned index = 0; index != 4; ++index) for (unsigned corrupt = 0; corrupt != 2; ++corrupt) {
    Fixture fixture;
    fixture.Establish();
    Storage* stores[] = {&fixture.a, &fixture.b, &fixture.c, &fixture.d};
    if (corrupt) stores[index]->bytes.back() ^= 0x80;
    else stores[index]->bytes.clear();
    const unsigned writes = fixture.Writes();
    DeviceStorageStartup startup(fixture.a, fixture.b, fixture.c, fixture.d, Identity());
    assert(!startup.Boot() && !startup.ImageStorageHealthy() && startup.RequiresService());
    fixture.Worker(false);
    assert(!startup.ControlJournal().BeginScoped(1, Id(3), 1, 100));
    assert(fixture.Writes() == writes);
  }
  Fixture fixture;
  fixture.Establish();
  auto wrong = Identity();
  wrong.deviceId = "device-002";
  const unsigned writes = fixture.Writes();
  DeviceStorageStartup startup(fixture.a, fixture.b, fixture.c, fixture.d, wrong);
  assert(!startup.Boot() && !startup.ImageStorageHealthy() && startup.RequiresService());
  assert(fixture.Writes() == writes);
}
}

int main() {
  MissingDoesNotCreateAnEmptyJournal();
  FoundUnscopedOriginMissingIsRetainedForExplicitService();
  EstablishedBootAdoptsTheReconciledEpochBeforeControl();
  PendingInspectionIsNotImageFailureOrBootResolution();
  EveryLostOrCorruptEstablishedStoreFailsClosed();
}

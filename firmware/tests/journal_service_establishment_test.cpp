#include <algorithm>
#include <cassert>
#include <cstring>
#include <iostream>
#include <vector>

#include "../src/features/device_control/JournalServiceEstablishment.h"
#include "../src/features/keepsake/KeepsakeCommittedInventory.h"
#include "../src/features/keepsake/KeepsakePersistenceCodec.h"
#include "../src/features/offline_show/OfflineCommittedInventory.h"

namespace {
using Mode = JournalServiceEstablishment::Mode;
using Result = JournalServiceEstablishment::Result;
const std::string Device = "service-test-001";
const std::array<uint8_t, DeviceIdentity::CredentialSize> Credential{{1, 2, 3}};

struct Schedule {
  size_t writes = 0, cutAt = 0;
  bool after = false;
  std::vector<std::string> order;
};
class Storage final : public JournalStorage {
 public:
  Storage(Schedule& schedule, const char* name) : schedule_(schedule), name_(name) {}
  JournalReadResult Read(uint8_t* target, size_t capacity, size_t& length) override {
    length = 0;
    if (failedRead || bytes.size() > capacity) return JournalReadResult::Failed;
    if (bytes.empty()) return JournalReadResult::Missing;
    std::copy(bytes.begin(), bytes.end(), target); length = bytes.size();
    return JournalReadResult::Found;
  }
  bool Write(const uint8_t* source, size_t length) override {
    schedule_.order.push_back(name_);
    const bool cut = ++schedule_.writes == schedule_.cutAt;
    if (cut && !schedule_.after) return false;
    bytes.assign(source, source + length);
    return !cut;
  }
  std::vector<uint8_t> bytes;
  bool failedRead = false;
 private:
  Schedule& schedule_;
  const char* name_;
};
class IdentityStorage final : public ProvisioningStorage {
 public:
  explicit IdentityStorage(Storage& storage) : storage_(storage) {}
  ProvisioningReadResult Read(uint8_t* data, size_t capacity, size_t& length) override {
    switch (storage_.Read(data, capacity, length)) {
      case JournalReadResult::Missing: return ProvisioningReadResult::Missing;
      case JournalReadResult::Found: return ProvisioningReadResult::Found;
      default: return ProvisioningReadResult::Failed;
    }
  }
  bool Write(const uint8_t* data, size_t length) override { return storage_.Write(data, length); }
 private:
  Storage& storage_;
};
class Gate final : public ServiceGate {
 public:
  bool ServiceModeActive() const override { return service; }
  bool ActuatorSupplyDisconnected() const override { return disconnected; }
  bool service = true, disconnected = true;
};
class Barrier final : public JournalReconciliationBarrier {
 public:
  bool TryEnter() override { if (held) return false; held = true; return true; }
  void Leave() override { assert(held); held = false; }
  bool held = false;
};
struct Durable {
  Schedule schedule;
  Storage journal{schedule, "journal"}, marker{schedule, "marker"};
  Storage online{schedule, "online"}, offline{schedule, "offline"}, device{schedule, "identity"};
  IdentityStorage identity{device};
  Gate gate;
  Barrier barrier;
};
struct Session {
  explicit Session(Durable& durable)
      : identity(durable.identity), journal(durable.journal), witness(durable.marker, journal),
        online(durable.online), offline(durable.offline),
        reconciliation(journal, witness, online, offline, durable.barrier),
        service(identity, durable.gate, witness, online, offline, reconciliation) {}
  DeviceIdentity identity;
  PersistentCommandJournal journal;
  JournalIdentityOwner witness;
  KeepsakeCommittedInventory online;
  kufr::offline::OfflineCommittedInventory offline;
  JournalReconciliation reconciliation;
  JournalServiceEstablishment service;
};
void FactoryAndCuts() {
  Durable baseline;
  {
    Session session(baseline);
    assert(session.service.Run(Mode::Factory, Device, &Credential) == Result::Established);
    assert((baseline.schedule.order == std::vector<std::string>{
        "marker", "journal", "online", "offline", "identity", "marker"}));
    assert(session.journal.CurrentEpoch() == 1 && !session.journal.HasPending());
  }
  // Every actual identity/witness/journal/inventory commit, on both sides of its
  // ACK. Reconstruct all owners/codecs before explicitly resuming preparation.
  for (size_t cut = 1; cut <= baseline.schedule.writes; ++cut) {
    for (bool after : {false, true}) {
      Durable durable;
      durable.schedule.cutAt = cut; durable.schedule.after = after;
      {
        Session session(durable);
        assert(session.service.Run(Mode::Factory, Device, &Credential) != Result::Established);
        const size_t writes = durable.schedule.writes;
        assert(session.service.Run(Mode::Resume, Device,
            session.identity.IsProvisioned() ? nullptr : &Credential) != Result::Established);
        assert(durable.schedule.writes == writes); // uncertain owners cannot retry in place
      }
      durable.schedule.cutAt = 0;
      {
        Session reopened(durable);
        const auto mode = reopened.witness.CurrentPhase() == JournalIdentityOwner::Phase::Missing
            ? Mode::Factory : Mode::Resume;
        assert(reopened.service.Run(mode, Device,
            reopened.identity.IsProvisioned() ? nullptr : &Credential) == Result::Established);
        assert(reopened.identity.IsProvisioned());
      }
      Session ready(durable);
      const size_t writes = durable.schedule.writes;
      assert(ready.service.Run(Mode::Resume, Device) == Result::Established);
      assert(durable.schedule.writes == writes);
      assert(ready.journal.CurrentEpoch() == 1 && !ready.journal.HasPending());
    }
  }
}
void EstablishedLoss() {
  for (unsigned store = 0; store < 5; ++store) {
    Durable durable;
    { Session initial(durable); assert(initial.service.Run(Mode::Factory, Device, &Credential) == Result::Established); }
    Storage* stores[] = {&durable.journal, &durable.marker, &durable.online, &durable.offline, &durable.device};
    stores[store]->bytes.clear();
    Session reopened(durable);
    const size_t writes = durable.schedule.writes;
    assert(reopened.service.Run(Mode::Resume, Device,
        reopened.identity.IsProvisioned() ? nullptr : &Credential) != Result::Established);
    assert(durable.schedule.writes == writes);
    assert(stores[store]->bytes.empty());
  }
}
void PrepareLegacy(Durable& durable, bool unknown) {
  Session session(durable);
  assert(session.identity.Provision(Device, Credential, durable.gate));
  assert(session.journal.PersistReadyMask(0x3f));
  const std::string encoded = keepsake_persistence::Encode(KeepsakeOutboxState{});
  durable.online.bytes.assign(encoded.begin(), encoded.end());
  kufr::offline::Outbox outbox;
  if (unknown) {
    auto& record = outbox.records[0];
    std::memcpy(record.operationId.data(), "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", 37);
    std::memcpy(record.bootId.data(), "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb", 37);
    std::memcpy(record.localEveningId.data(), "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee", 37);
    record.slot = 1; record.startedAtMs = 1;
  }
  assert(session.offline.Write(outbox));
}
void LegacyPreservation() {
  for (bool unknown : {false, true}) {
    Durable durable;
    PrepareLegacy(durable, unknown);
    Session session(durable);
    assert(session.service.Run(Mode::Legacy, Device) ==
        (unknown ? Result::EvidenceBlocked : Result::Established));
    assert(session.witness.CurrentPhase() == JournalIdentityOwner::Phase::Established);
    kufr::offline::Outbox readback;
    assert(session.offline.Read(readback));
    assert(readback.records[0].operationEpoch == 0 && readback.records[0].physicalFingerprint == 0);
    if (unknown) assert(readback.records[0].operationId[0] == 'a');
    const size_t writes = durable.schedule.writes;
    assert(session.service.Run(Mode::Resume, Device) ==
        (unknown ? Result::EvidenceBlocked : Result::Established));
    assert(durable.schedule.writes == writes);
  }
  for (bool onlineMissing : {false, true}) {
    Durable durable;
    PrepareLegacy(durable, false);
    (onlineMissing ? durable.online : durable.offline).bytes.clear();
    Session session(durable);
    const size_t writes = durable.schedule.writes;
    assert(session.service.Run(Mode::Legacy, Device) == Result::InventoryRejected);
    assert(durable.schedule.writes == writes && durable.marker.bytes.empty());
  }
}
void LegacyCuts() {
  for (bool unknown : {false, true}) {
    // Adoption has five commits: witness, journal, both bindings, final witness.
    for (size_t cut = 1; cut <= 5; ++cut) {
      for (bool after : {false, true}) {
        Durable durable;
        PrepareLegacy(durable, unknown);
        durable.schedule.cutAt = durable.schedule.writes + cut;
        durable.schedule.after = after;
        {
          Session session(durable);
          const auto result = session.service.Run(Mode::Legacy, Device);
          assert(result != Result::Established);
        }
        durable.schedule.cutAt = 0;
        Session reopened(durable);
        const auto mode = reopened.witness.CurrentPhase() == JournalIdentityOwner::Phase::Missing
            ? Mode::Legacy : Mode::Resume;
        assert(reopened.service.Run(mode, Device) ==
            (unknown ? Result::EvidenceBlocked : Result::Established));
        kufr::offline::Outbox outbox;
        assert(reopened.offline.Read(outbox));
        assert(outbox.records[0].operationEpoch == 0 && outbox.records[0].physicalFingerprint == 0);
        if (unknown) assert(outbox.records[0].operationId[0] == 'a');
        assert(reopened.identity.DeviceId() == Device);
      }
    }
  }
}
void Refusals() {
  Durable durable;
  {
    Session session(durable);
    durable.gate.service = false;
    assert(session.service.Run(Mode::Factory, Device, &Credential) == Result::ServiceDenied);
    durable.gate.service = true; durable.gate.disconnected = false;
    assert(session.service.Run(Mode::Factory, Device, &Credential) == Result::ServiceDenied);
    durable.gate.disconnected = true;
    assert(session.service.Run(Mode::Resume, Device, &Credential) == Result::MarkerRejected);
    assert(session.service.Run(Mode::Factory, Device) == Result::InvalidRequest);
    const std::array<uint8_t, DeviceIdentity::CredentialSize> zero{};
    assert(session.service.Run(Mode::Factory, Device, &zero) == Result::InvalidRequest);
    assert(durable.schedule.writes == 0);
    assert(session.service.Run(Mode::Factory, Device, &Credential) == Result::Established);
  }
  Session ready(durable);
  const size_t writes = durable.schedule.writes;
  assert(ready.service.Run(Mode::Resume, "other-device") == Result::IdentityReplacementDenied);
  assert(ready.service.Run(Mode::Resume, Device, &Credential) == Result::IdentityReplacementDenied);
  durable.barrier.held = true;
  assert(ready.service.Run(Mode::Resume, Device) == Result::EvidenceBlocked);
  assert(durable.schedule.writes == writes);
}
}
int main() {
  FactoryAndCuts(); EstablishedLoss(); LegacyPreservation(); LegacyCuts(); Refusals();
  std::cout << "journal service establishment checks passed\n";
}

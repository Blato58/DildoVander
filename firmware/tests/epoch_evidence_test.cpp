#include <cassert>
#include <algorithm>
#include <iostream>
#include <vector>
#include "../src/features/device_control/PersistentCommandJournal.h"
#include "../src/features/keepsake/KeepsakePersistenceCodec.h"

namespace {
class Storage final : public JournalStorage {
 public:
  JournalReadResult Read(uint8_t* target, size_t capacity, size_t& length) override {
    if (bytes.empty()) return JournalReadResult::Missing;
    if (bytes.size() > capacity) return JournalReadResult::Failed;
    length = bytes.size(); std::copy(bytes.begin(), bytes.end(), target);
    return JournalReadResult::Found;
  }
  bool Write(const uint8_t* source, size_t length) override {
    bytes.assign(source, source + length); return true;
  }
  std::vector<uint8_t> bytes;
};
KeepsakeOutcome Candidate(uint64_t epoch = 1) {
  KeepsakeOutcome item{"11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222",
      "33333333-3333-4333-8333-333333333333", "33333333-3333-4333-8333-333333333333",
      "44444444-4444-4444-8444-444444444444", 1, 2, 0};
  item.compartment = 3; item.operationEpoch = epoch; item.physicalFingerprint = epoch ? 101 : 0;
  return item;
}
KeepsakeOutboxState Decode(const std::string& bytes) {
  KeepsakeOutboxState state;
  assert(keepsake_persistence::Decode(bytes.c_str(), bytes.size(), state));
  return state;
}
void LegacyAndCanonicalIdentity() {
  const auto legacy = Candidate(0);
  const std::string body = keepsake_persistence::Body(legacy, true);
  for (const auto& bytes : {"[" + body + "]",
      "{\"schemaVersion\":1,\"pending\":[" + body + "],\"quarantined\":[]}",
      "{\"schemaVersion\":2,\"pending\":[],\"quarantined\":[],\"prepared\":[" + body + "]}"}) {
    const auto state = Decode(bytes);
    const auto& item = state.pending.empty() ? state.prepared.front() : state.pending.front();
    assert(item.operationEpoch == 0 && item.physicalFingerprint == 0 && item.compartment == 3);
  }
  auto maximum = Candidate(UINT64_MAX);
  maximum.physicalFingerprint = UINT64_MAX;
  const auto encoded = keepsake_persistence::Encode({{maximum}, {}, {maximum}});
  const auto decoded = Decode(encoded);
  assert(SameKeepsakeCandidate(decoded.pending.front(), maximum));
  auto missingSlot = maximum; missingSlot.compartment = 0;
  const auto invalidSlotBytes = keepsake_persistence::Encode({{missingSlot}, {}, {}});
  KeepsakeOutboxState invalidSlotState;
  assert(!keepsake_persistence::Decode(invalidSlotBytes.c_str(), invalidSlotBytes.size(), invalidSlotState));
  const auto valueAt = encoded.find("18446744073709551615");
  assert(valueAt != std::string::npos);
  for (const char* invalid : {"18446744073709551616", "01", "-1", "+1", "1.0", "", " 1", "1e1"}) {
    auto malformed = encoded;
    malformed.replace(valueAt, 20, invalid);
    KeepsakeOutboxState rejected;
    assert(!keepsake_persistence::Decode(malformed.c_str(), malformed.size(), rejected));
  }
  auto numeric = encoded;
  numeric.replace(valueAt - 1, 22, "1");
  KeepsakeOutboxState rejected;
  assert(!keepsake_persistence::Decode(numeric.c_str(), numeric.size(), rejected));
  const auto response = std::string("{\"locator\":\"55555555-5555-4555-8555-555555555555\",\"outcomeId\":\"")
      + maximum.outcomeId + "\",\"physicalOperationId\":\"" + maximum.physicalOperationId
      + "\",\"sessionEpoch\":1,\"operationEpoch\":\"18446744073709551615\",\"physicalFingerprint\":\"18446744073709551615\"}";
  assert(keepsake_persistence::MatchesOutcomeAcknowledgement(response.c_str(), response.size(), maximum));
  auto foreign = maximum; --foreign.operationEpoch;
  assert(!keepsake_persistence::MatchesOutcomeAcknowledgement(response.c_str(), response.size(), foreign));
  foreign = maximum; --foreign.physicalFingerprint;
  assert(!keepsake_persistence::MatchesOutcomeAcknowledgement(response.c_str(), response.size(), foreign));
}
void LegacyPhysicalProofSurvivesAdoption() {
  Storage storage;
  storage.bytes.assign(44 + PersistentCommandJournal::HistoryCapacity * 45, 0);
  storage.bytes[0] = 'K'; storage.bytes[1] = 'J'; storage.bytes[2] = 'R'; storage.bytes[3] = '1';
  storage.bytes[4] = 3; storage.bytes[5] = 63; storage.bytes[7] = 1;
  const auto legacy = Candidate(0);
  std::copy(legacy.physicalOperationId.begin(), legacy.physicalOperationId.end(), storage.bytes.begin() + 44);
  storage.bytes[44 + 36] = static_cast<uint8_t>(legacy.compartment | 0x80);
  storage.bytes[44 + 37] = 101;
  PersistentCommandJournal journal(storage);
  assert(journal.IsHealthy() && HasKeepsakePhysicalEvidence(journal, legacy));
  assert(journal.EstablishScopedIdentity("device"));
  assert(HasKeepsakePhysicalEvidence(journal, legacy));
  auto current = Candidate(1);
  assert(!HasKeepsakePhysicalEvidence(journal, current));
  assert(journal.BeginScoped(1, current.physicalOperationId, current.compartment, current.physicalFingerprint));
  assert(journal.ResolveRestored(current.compartment));
  assert(!HasKeepsakePhysicalEvidence(journal, current));
  KeepsakeOutbox outbox;
  assert(outbox.Prepare(legacy) && outbox.Prepare(current));
  const auto persisted = Decode(keepsake_persistence::Encode(outbox.State()));
  KeepsakeOutbox rebooted;
  assert(rebooted.Restore(persisted.pending, persisted.quarantined, persisted.prepared));
  PersistentCommandJournal reopened(storage);
  assert(rebooted.RecoverPrepared(reopened));
  assert(rebooted.Snapshot().size() == 1 && rebooted.Snapshot().front().operationEpoch == 0);
  assert(rebooted.HasDisposition(current.outcomeId, 1));
}
void ExactHistoricalEvidence() {
  Storage storage;
  PersistentCommandJournal journal(storage);
  assert(journal.PersistReadyMask(63) && journal.EstablishScopedIdentity("device", true));
  auto first = Candidate();
  assert(journal.BeginScoped(1, first.physicalOperationId, first.compartment, first.physicalFingerprint));
  assert(journal.ResolveRestored(first.compartment));
  assert(!HasKeepsakePhysicalEvidence(journal, first));
  assert(journal.ConfirmTimedPresentationScoped(1, first.physicalOperationId, first.compartment, first.physicalFingerprint));
  assert(HasKeepsakePhysicalEvidence(journal, first));
  for (unsigned field = 0; field < 4; ++field) {
    auto falseEvidence = first;
    if (field == 0) ++falseEvidence.operationEpoch;
    if (field == 1) ++falseEvidence.compartment;
    if (field == 2) ++falseEvidence.physicalFingerprint;
    if (field == 3) falseEvidence.physicalFingerprint = 0;
    assert(!HasKeepsakePhysicalEvidence(journal, falseEvidence));
  }
  assert(journal.AdvanceEpochAndCompact()); // Explicit test setup only; retained proof remains pinned.
  auto second = Candidate(2);
  assert(journal.BeginScoped(2, second.physicalOperationId, second.compartment, 101));
  assert(journal.ResolveRestored(second.compartment));
  assert(!HasKeepsakePhysicalEvidence(journal, second));
  KeepsakeOutbox prepared;
  assert(prepared.Prepare(first) && prepared.Prepare(second));
  const auto bytes = keepsake_persistence::Encode(prepared.State());
  PersistentCommandJournal rebootedJournal(storage);
  const auto state = Decode(bytes);
  KeepsakeOutbox rebooted;
  assert(rebooted.Restore(state.pending, state.quarantined, state.prepared));
  assert(rebooted.RecoverPrepared(rebootedJournal));
  assert(rebooted.Snapshot().size() == 1 && SameKeepsakeCandidate(rebooted.Snapshot().front(), first));
  assert(rebooted.HasDisposition(second.outcomeId, 2) && !rebooted.HasDisposition(first.outcomeId, 1));
}
void ReusedIdsAndDurableAcknowledgementCuts() {
  for (unsigned cut = 1; cut <= 3; ++cut) {
    for (bool lostAcknowledgement : {false, true}) {
      KeepsakeOutbox outbox;
      const auto first = Candidate(1), second = Candidate(2);
      for (const auto& item : {first, second}) {
        assert(outbox.Prepare(item));
        assert(outbox.Queue(item) == KeepsakeQueueResult::VolatilePendingPersistence);
      }
      auto conflict = first; ++conflict.physicalFingerprint;
      assert(!outbox.Prepare(conflict) && !outbox.RequestDeliveryAcknowledgement(conflict));
      assert(outbox.Queue(conflict) == KeepsakeQueueResult::Rejected);
      assert(outbox.RequestDeliveryAcknowledgement(first));
      std::string durable = keepsake_persistence::Encode(outbox.State());
      unsigned writes = 0;
      assert(!outbox.ProcessDeliveryAcknowledgements([&](const KeepsakeOutboxState& state) {
        const bool fail = ++writes == cut;
        if (!fail || lostAcknowledgement) durable = keepsake_persistence::Encode(state);
        return !fail;
      }));
      const auto state = Decode(durable);
      KeepsakeOutbox rebooted;
      assert(rebooted.Restore(state.pending, state.quarantined, state.prepared));
      const auto retained = rebooted.Snapshot();
      assert(std::any_of(retained.begin(), retained.end(), [&](const KeepsakeOutcome& item) {
        return SameKeepsakeCandidate(item, second);
      }));
      assert(retained.size() == (cut == 1 && !lostAcknowledgement ? 2u : 1u));
      rebooted.Complete(second.outcomeId); // A legacy default cannot remove epoch two.
      assert(rebooted.Snapshot().size() == retained.size());
    }
  }
}
}
int main() {
  LegacyAndCanonicalIdentity();
  LegacyPhysicalProofSurvivesAdoption();
  ExactHistoricalEvidence();
  ReusedIdsAndDurableAcknowledgementCuts();
  std::cout << "Epoch-scoped durable evidence, legacy codec migration and acknowledgement cuts passed\n";
}

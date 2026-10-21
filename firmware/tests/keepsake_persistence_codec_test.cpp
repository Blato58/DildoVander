#include <cassert>
#include <iostream>
#include <vector>
#include "../src/features/keepsake/KeepsakePersistenceCodec.h"

class NoCompletionJournal final : public CommandJournal {
 public:
  bool HasPending() const override { return false; }
  uint8_t PendingCompartment() const override { return 0; }
  uint8_t ReadyMask() const override { return 63; }
  bool Begin(const std::string&, uint8_t) override { return false; }
  bool Resolve(uint8_t) override { return false; }
  bool PersistReadyMask(uint8_t) override { return false; }
};

KeepsakeOutcome Candidate() {
  KeepsakeOutcome value{"11111111-1111-4111-8111-111111111111", "22222222-2222-4222-8222-222222222222",
      "33333333-3333-4333-8333-333333333333", "33333333-3333-4333-8333-333333333333",
      "44444444-4444-4444-8444-444444444444", 1, 2, 0};
  value.compartment = 3;
  return value;
}

void StrictTechnicalAcknowledgements() {
  auto outcome = Candidate();
  outcome.operationEpoch = 18446744073709551615ULL;
  outcome.physicalFingerprint = 9007199254740993ULL;
  const std::string response = "{\"schemaVersion\":1,\"outcomeId\":\"" + outcome.outcomeId +
      "\",\"physicalOperationId\":\"" + outcome.physicalOperationId +
      "\",\"sessionEpoch\":1,\"operationEpoch\":\"18446744073709551615\",\"physicalFingerprint\":\"9007199254740993\"}";
  const auto technical = [&](const std::string& value, const KeepsakeOutcome& expected) {
    return keepsake_persistence::MatchesTechnicalOutcomeAcknowledgement(value.c_str(), value.size(), expected);
  };
  assert(technical(response, outcome));
  assert(!keepsake_persistence::MatchesOutcomeAcknowledgement(response.c_str(), response.size(), outcome));
  for (unsigned changed = 0; changed != 5; ++changed) {
    auto wrong = outcome;
    if (changed == 0) --wrong.operationEpoch;
    if (changed == 1) --wrong.physicalFingerprint;
    if (changed == 2) ++wrong.generation;
    if (changed == 3) wrong.physicalOperationId = "77777777-7777-4777-8777-777777777777";
    if (changed == 4) wrong.outcomeId = "66666666-6666-4666-8666-666666666666";
    assert(!technical(response, wrong));
  }
  auto located = response.substr(0, response.size() - 1) + ",\"locator\":\"55555555-5555-4555-8555-555555555555\"}";
  assert(!technical(located, outcome));
  const std::string production = "{\"locator\":\"55555555-5555-4555-8555-555555555555\",\"outcomeId\":\""
      + outcome.outcomeId + "\",\"physicalOperationId\":\"" + outcome.physicalOperationId
      + "\",\"sessionEpoch\":1,\"operationEpoch\":\"18446744073709551615\",\"physicalFingerprint\":\"9007199254740993\"}";
  assert(keepsake_persistence::MatchesOutcomeAcknowledgement(production.c_str(), production.size(), outcome));
  assert(!technical(production, outcome));
  for (const auto& replacement : {std::pair<std::string, std::string>{"\"schemaVersion\":1", "\"schemaVersion\":2"},
      {"\"operationEpoch\":\"18446744073709551615\"", "\"operationEpoch\":18446744073709551615"},
      {"\"operationEpoch\":\"18446744073709551615\"", "\"operationEpoch\":\"0\""},
      {"\"physicalFingerprint\":\"9007199254740993\"", "\"physicalFingerprint\":\"0\""},
      {"\"sessionEpoch\":1", "\"sessionEpoch\":\"1\""}}) {
    auto invalid = response;
    invalid.replace(invalid.find(replacement.first), replacement.first.size(), replacement.second);
    assert(!technical(invalid, outcome));
  }
  for (const auto& invalid : {std::string("null"), std::string("[]"), std::string("{}"), response.substr(0, response.size() - 1)})
    assert(!technical(invalid, outcome));
}

int main() {
  StrictTechnicalAcknowledgements();
  const std::string response = "{\"locator\":\"55555555-5555-4555-8555-555555555555\",\"outcomeId\":\""
      + Candidate().outcomeId + "\",\"physicalOperationId\":\"" + Candidate().physicalOperationId + "\",\"sessionEpoch\":1}";
  assert(keepsake_persistence::MatchesOutcomeAcknowledgement(response.c_str(), response.size(), Candidate()));
  auto wrong = Candidate();
  wrong.outcomeId = "66666666-6666-4666-8666-666666666666";
  assert(!keepsake_persistence::MatchesOutcomeAcknowledgement(response.c_str(), response.size(), wrong));
  wrong = Candidate();
  ++wrong.generation;
  assert(!keepsake_persistence::MatchesOutcomeAcknowledgement(response.c_str(), response.size(), wrong));
  wrong = Candidate();
  wrong.physicalOperationId = "77777777-7777-4777-8777-777777777777";
  assert(!keepsake_persistence::MatchesOutcomeAcknowledgement(response.c_str(), response.size(), wrong));
  NoCompletionJournal journal;
  const char* reasons[] = {"proposal_store_rejected", "proposal_stale_or_expired", "proposal_stopped",
      "proposal_cancelled", "presentation_fault", "proposal_start_rejected", "conversation_closed",
      "completion_unconfirmed"};
  for (const auto* reason : reasons) {
    KeepsakeOutbox outbox;
    assert(outbox.Prepare(Candidate()) && outbox.RequestRetirement(Candidate(), reason));
    std::vector<std::string> committed;
    assert(outbox.ProcessRetirements(journal, [&](const KeepsakeOutboxState& state) {
      committed.push_back(keepsake_persistence::Encode(state));
      return true;
    }));
    assert(committed.size() == 3);
    // Reset after either durable fence or durable removal, before compaction.
    for (size_t boundary = 0; boundary < 2; ++boundary) {
      KeepsakeOutboxState decoded;
      const auto& bytes = committed[boundary];
      assert(keepsake_persistence::Decode(bytes.c_str(), bytes.size() + 1, decoded));
      assert(decoded.quarantined.size() == 1 && decoded.quarantined.front().reason == reason);
      assert(decoded.prepared.size() == (boundary == 0 ? 1u : 0u));
      KeepsakeOutbox rebooted;
      assert(rebooted.Restore(decoded.pending, decoded.quarantined, decoded.prepared));
      assert(rebooted.State().prepared.empty() && rebooted.Snapshot().empty());
      // Boot commits fenced removal before compacting the temporary proposal disposition.
      const auto removedBytes = keepsake_persistence::Encode(rebooted.State());
      assert(keepsake_persistence::Decode(removedBytes.c_str(), removedBytes.size() + 1, decoded));
      assert(decoded.prepared.empty() && decoded.quarantined.size() == 1);
      rebooted.CompactPreparationDispositions();
      assert(rebooted.State().quarantined.empty() && rebooted.Prepare(Candidate()));
    }
    auto unknown = committed.front();
    const auto offset = unknown.find(reason);
    assert(offset != std::string::npos);
    unknown.replace(offset, std::strlen(reason), "arbitrary_unapproved_reason");
    KeepsakeOutboxState rejected;
    assert(!keepsake_persistence::Decode(unknown.c_str(), unknown.size() + 1, rejected));
  }
  KeepsakeOutbox delivery;
  assert(delivery.Prepare(Candidate()));
  assert(delivery.Queue(Candidate()) == KeepsakeQueueResult::VolatilePendingPersistence);
  assert(delivery.RequestDeliveryAcknowledgement(Candidate()));
  std::vector<std::string> ackBytes;
  assert(delivery.ProcessDeliveryAcknowledgements([&](const KeepsakeOutboxState& state) {
    ackBytes.push_back(keepsake_persistence::Encode(state));
    return true;
  }));
  assert(ackBytes.size() == 3);
  for (size_t boundary = 0; boundary < 2; ++boundary) {
    KeepsakeOutboxState decoded;
    assert(keepsake_persistence::Decode(ackBytes[boundary].c_str(), ackBytes[boundary].size() + 1, decoded));
    assert(decoded.quarantined.size() == 1 && decoded.quarantined.front().reason == "outcome_delivered");
    KeepsakeOutbox rebooted;
    assert(rebooted.Restore(decoded.pending, decoded.quarantined, decoded.prepared));
    assert(rebooted.Snapshot().empty() && rebooted.State().prepared.empty());
    const auto removal = keepsake_persistence::Encode(rebooted.State());
    assert(keepsake_persistence::Decode(removal.c_str(), removal.size() + 1, decoded));
    rebooted.CompactPreparationDispositions();
    assert(rebooted.State().quarantined.empty());
  }
  std::cout << "Production NVS codec restores every temporary retirement fence and rejects unknown reasons\n";
}

#include <cassert>
#include <iostream>
#include "../src/features/keepsake/KeepsakeOutbox.h"

KeepsakeOutcome Outcome(const char* id = "outcome") {
  return {"backend-operation", id, "proposal", "physical-operation", "boot", 12, 41, 0};
}

void ExactAcknowledgementPersistsBeforeRemoval() {
  KeepsakeOutbox outbox;
  for (size_t i = 0; i < KeepsakeOutbox::Capacity; ++i) {
    auto candidate = Outcome();
    candidate.operationId += std::to_string(i);
    candidate.outcomeId += std::to_string(i);
    assert(outbox.Prepare(candidate));
    assert(outbox.Queue(candidate) == KeepsakeQueueResult::VolatilePendingPersistence);
  }
  const auto accepted = outbox.Snapshot().front();
  auto foreign = accepted;
  foreign.physicalOperationId = "foreign-physical-operation";
  assert(!outbox.RequestDeliveryAcknowledgement(foreign));
  assert(outbox.RequestDeliveryAcknowledgement(accepted));
  assert(outbox.RequestDeliveryAcknowledgement(accepted));
  KeepsakeOutcome ready;
  assert(outbox.NextReady(0, ready) && ready.outcomeId != accepted.outcomeId);
  assert(!outbox.ProcessDeliveryAcknowledgements([](const KeepsakeOutboxState&) { return false; }));
  assert(outbox.Snapshot().size() == KeepsakeOutbox::Capacity);
  KeepsakeOutboxState durableAck;
  unsigned writes = 0;
  assert(!outbox.ProcessDeliveryAcknowledgements([&](const KeepsakeOutboxState& state) {
    if (++writes == 2) return false;
    durableAck = state;
    return true;
  }));
  assert(durableAck.pending.size() == KeepsakeOutbox::Capacity && durableAck.prepared.size() == KeepsakeOutbox::Capacity);
  assert(durableAck.quarantined.size() == 1 && durableAck.quarantined.front().reason == "outcome_delivered");
  assert(outbox.Snapshot().size() == KeepsakeOutbox::Capacity - 1);
  auto next = Outcome("new-outcome");
  next.operationId = "new-operation";
  assert(!outbox.Prepare(next));  // Failed removal commit still owns the eighth slot.
  KeepsakeOutbox rebooted;
  assert(rebooted.Restore(durableAck.pending, durableAck.quarantined, durableAck.prepared));
  assert(rebooted.Snapshot().size() == KeepsakeOutbox::Capacity - 1);
  for (const auto& retained : rebooted.Snapshot()) assert(retained.outcomeId != accepted.outcomeId);
  assert(outbox.ProcessDeliveryAcknowledgements([](const KeepsakeOutboxState&) { return true; }));
  assert(outbox.State().quarantined.empty() && outbox.Prepare(next));
  assert(!outbox.RequestDeliveryAcknowledgement(accepted));  // Repeated ACK cannot retire another guest.
  assert(outbox.Snapshot().size() == KeepsakeOutbox::Capacity - 1);

  // Reset before any ACK commit retries the same identity; it cannot mint another outcome.
  KeepsakeOutbox beforeAckCrash;
  assert(beforeAckCrash.Restore({accepted}, {}, {accepted}));
  assert(beforeAckCrash.Queue(accepted) == KeepsakeQueueResult::AlreadyQueued);
  assert(beforeAckCrash.Snapshot().size() == 1 && beforeAckCrash.Snapshot().front().outcomeId == accepted.outcomeId);
  assert(beforeAckCrash.RequestDeliveryAcknowledgement(accepted));
  assert(beforeAckCrash.ProcessDeliveryAcknowledgements([](const KeepsakeOutboxState&) { return true; }));
  assert(beforeAckCrash.Snapshot().empty() && beforeAckCrash.State().prepared.empty());
  KeepsakeOutbox evening;
  for (size_t i = 0; i < KeepsakeOutbox::QuarantineCapacity * 3; ++i) {
    auto candidate = Outcome();
    candidate.operationId += std::to_string(i);
    candidate.outcomeId += std::to_string(i);
    assert(evening.Prepare(candidate));
    assert(evening.Queue(candidate) == KeepsakeQueueResult::VolatilePendingPersistence);
    assert(evening.RequestDeliveryAcknowledgement(candidate));
    assert(evening.ProcessDeliveryAcknowledgements([](const KeepsakeOutboxState&) { return true; }));
    assert(evening.Snapshot().empty() && evening.State().prepared.empty() && evening.State().quarantined.empty());
  }
}

int main() {
  ExactAcknowledgementPersistsBeforeRemoval();
  KeepsakeOutbox outbox;
  assert(outbox.Queue(Outcome()) == KeepsakeQueueResult::VolatilePendingPersistence);
  const auto queued = outbox.Snapshot().front();
  outbox.Complete("foreign-outcome");
  assert(outbox.Snapshot().size() == 1);
  outbox.Complete("outcome");
  assert(outbox.Snapshot().empty());

  KeepsakeOutbox rebooted;
  assert(rebooted.Restore({queued}));
  assert(rebooted.Queue(Outcome()) == KeepsakeQueueResult::AlreadyQueued);
  assert(rebooted.Queue(Outcome("conflicting-outcome")) == KeepsakeQueueResult::Rejected);
  assert(rebooted.Snapshot().size() == 1);
  KeepsakeOutbox corrupt;
  assert(!corrupt.Restore({queued, queued}));
  KeepsakeOutbox full;
  for (size_t i = 0; i < KeepsakeOutbox::Capacity; ++i) {
    auto next = Outcome();
    next.operationId += std::to_string(i);
    next.outcomeId += std::to_string(i);
    assert(full.Queue(next) == KeepsakeQueueResult::VolatilePendingPersistence);
  }
  auto overflow = Outcome("overflow");
  overflow.operationId = "overflow-operation";
  assert(full.Queue(overflow) == KeepsakeQueueResult::Rejected);
  assert(full.Snapshot().size() == KeepsakeOutbox::Capacity);
  assert(PermanentKeepsakeFailure(410, "outcome_deleted"));
  assert(PermanentKeepsakeFailure(409, "outcome_not_eligible"));
  assert(PermanentKeepsakeFailure(409, "outcome_conflict"));
  assert(PermanentKeepsakeFailure(409, "operator_restore_required"));
  assert(!PermanentKeepsakeFailure(409, "unknown_error"));
  assert(!PermanentKeepsakeFailure(404, ""));
  assert(!PermanentKeepsakeFailure(500, "outcome_deleted"));

  KeepsakeOutbox dispositions;
  auto deleted = Outcome("deleted");
  auto eligible = Outcome("eligible");
  eligible.operationId = "another-backend-operation";
  assert(dispositions.Queue(deleted) == KeepsakeQueueResult::VolatilePendingPersistence);
  assert(dispositions.Queue(eligible) == KeepsakeQueueResult::VolatilePendingPersistence);
  const auto deletedQueued = dispositions.Snapshot().front();
  assert(dispositions.RecordDisposition(deletedQueued, "outcome_deleted"));
  const auto persistedDisposition = dispositions.State();
  assert(persistedDisposition.pending.size() == 2);  // Persistence precedes removal.
  assert(persistedDisposition.quarantined.size() == 1);
  KeepsakeOutbox afterDispositionCrash;
  assert(afterDispositionCrash.Restore(persistedDisposition.pending, persistedDisposition.quarantined));
  assert(afterDispositionCrash.Snapshot().size() == 1);
  assert(afterDispositionCrash.State().quarantined.front().outcomeId == "deleted");
  assert(afterDispositionCrash.Queue(deleted) == KeepsakeQueueResult::Rejected);
  KeepsakeOutcome ready;
  assert(afterDispositionCrash.NextReady(0, ready) && ready.outcomeId == "eligible");

  KeepsakeOutbox retries;
  assert(retries.Queue(deleted) == KeepsakeQueueResult::VolatilePendingPersistence);
  assert(retries.Queue(eligible) == KeepsakeQueueResult::VolatilePendingPersistence);
  retries.Defer("deleted", 100);
  assert(retries.NextReady(100, ready) && ready.outcomeId == "eligible");
  retries.Complete("eligible");
  assert(retries.Snapshot().size() == 1 && retries.Snapshot().front().outcomeId == "deleted");
  assert(!retries.NextReady(2099, ready));
  assert(retries.NextReady(2100, ready) && ready.outcomeId == "deleted");
  for (size_t i = 0; i < 100; ++i) retries.Defer("deleted", 10000);
  assert(retries.Snapshot().front().retryDelayMs == 30000);
  assert(retries.Snapshot().front().outcomeId == "deleted");  // No TTL/LRU discard.
  KeepsakeOutbox quarantineFull;
  for (size_t i = 0; i < KeepsakeOutbox::QuarantineCapacity; ++i) {
    auto terminal = Outcome();
    terminal.operationId += std::to_string(i);
    terminal.outcomeId += std::to_string(i);
    assert(quarantineFull.RecordDisposition(terminal, "outcome_deleted"));
  }
  assert(!quarantineFull.RecordDisposition(overflow, "outcome_deleted"));
  assert(quarantineFull.Queue(overflow) == KeepsakeQueueResult::Rejected);
  assert(quarantineFull.State().quarantined.size() == KeepsakeOutbox::QuarantineCapacity);
  std::cout << "Keepsake outbox isolation, volatile admission, durable dispositions, fair retry and capacity passed\n";
}

#include <algorithm>
#include <cassert>
#include <vector>

#include "../src/features/device_control/AsyncCommandJournal.h"
#include "../src/features/device_control/PersistentCommandJournal.h"

namespace {
constexpr char Id[] = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
class Storage final : public JournalStorage {
 public:
  JournalReadResult Read(uint8_t* output, size_t capacity, size_t& length) override {
    length = bytes.size();
    if (bytes.empty()) return JournalReadResult::Missing;
    if (length > capacity) return JournalReadResult::Failed;
    std::copy(bytes.begin(), bytes.end(), output);
    return JournalReadResult::Found;
  }
  bool Write(const uint8_t* input, size_t length) override {
    ++writes;
    if (cutBefore) return false;
    bytes.assign(input, input + length);
    return !cutAfter;
  }
  std::vector<uint8_t> bytes;
  size_t writes = 0;
  bool cutBefore = false, cutAfter = false;
};
void ExactResultAndOriginalEpoch() {
  Storage storage;
  PersistentCommandJournal core(storage);
  assert(core.EstablishScopedIdentity("device-001", true));
  assert(core.PersistReadyMask(63));
  JournalTransactionArbiter arbiter;
  JournalStorageOwner owner(core, arbiter);
  AsyncCommandJournal journal(owner, arbiter);
  const auto initialWrites = storage.writes;
  assert(!journal.BeginScoped(1, Id, 2, 77));
  assert(journal.LastStorageState() == CommandJournal::StorageState::Pending);
  assert(journal.HasPendingStorage() && !journal.HasPending());
  assert(!journal.BeginScoped(2, Id, 2, 77));
  assert(journal.LastStorageState() == CommandJournal::StorageState::Rejected);
  assert(storage.writes == initialWrites && !arbiter.TryEnter());
  assert(owner.RunOne());
  journal.PollStorage();
  assert(journal.HasPending() && journal.HasPendingStorage());
  assert(journal.BeginScoped(1, Id, 2, 77));
  assert(!journal.HasPendingStorage() && journal.CurrentEpoch() == 1);
  assert(storage.writes == initialWrites + 1);
  assert(!journal.ResolveRestored(2));
  assert(owner.RunOne());
  assert(journal.ResolveRestored(2));
  assert(!journal.HasPending());
  assert(!journal.WasTimedPresentationCompletedScoped(1, Id, 2, 77));
}
void CancelledSuccessIsAdopted() {
  for (bool beforeWrite : {false, true}) {
    Storage storage;
    PersistentCommandJournal core(storage);
    assert(core.EstablishScopedIdentity("device-001", true));
    assert(core.PersistReadyMask(63));
    JournalTransactionArbiter arbiter;
    JournalStorageOwner owner(core, arbiter);
    AsyncCommandJournal journal(owner, arbiter);
    assert(!journal.BeginScoped(1, Id, 2, 77));
    if (!beforeWrite) assert(owner.RunOne());
    journal.CancelStorageAdmissions();
    if (beforeWrite) assert(owner.RunOne());
    journal.PollStorage();
    assert(!journal.HasPendingStorage() && !journal.CanBeginOperation());
    assert(journal.HasPending() == !beforeWrite);
    assert(core.HasPending() == !beforeWrite);
    if (journal.HasPending()) {
      journal.BeginStorageRecovery();
      assert(!journal.Resolve(61));
      assert(owner.RunOne());
      assert(journal.Resolve(61));
    } else {
      journal.BeginStorageRecovery();
    }
    assert(journal.ResumeStorageAfterRecovery());
    assert(!journal.HasPending() && journal.CanBeginOperation());
  }
}
void ReservationCancellationAndRecoveryFence() {
  Storage storage;
  PersistentCommandJournal core(storage);
  assert(core.EstablishScopedIdentity("device-001", true));
  assert(core.PersistReadyMask(63));
  JournalTransactionArbiter arbiter;
  JournalStorageOwner owner(core, arbiter);
  AsyncCommandJournal journal(owner, arbiter);
  const auto generation = arbiter.Cancellation();
  journal.CancelPresentationReservation();
  assert(arbiter.Cancellation() == generation); // no unrelated recovery invalidation
  assert(!journal.ReservePresentation(1, Id, 2, 77));
  assert(owner.RunOne());
  assert(journal.ReservePresentation(1, Id, 2, 77));
  assert(journal.HasPresentationReservation());
  journal.CancelPresentationReservation();
  assert(!journal.HasPresentationReservation());
  journal.BeginStorageRecovery();
  journal.CancelStorageAdmissions(); // idle Stop also defeats inspected recovery
  assert(!journal.ResumeStorageAfterRecovery());
  assert(owner.RunOne()); // worker observes the volatile capability invalidation
  journal.PollStorage();
  assert(!journal.HasPresentationReservation() && !core.HasPresentationReservation());
  assert(core.FindHistorical(1, Id, 2, 77) == CommandJournal::Lookup::Same);
  assert(!core.WasTimedPresentationCompletedHistorical(1, Id, 2, 77));
}
void LostAckIsNeverReplayedAsSuccess() {
  for (bool afterWrite : {false, true}) {
    Storage storage;
    PersistentCommandJournal core(storage);
    assert(core.EstablishScopedIdentity("device-001", true));
    assert(core.PersistReadyMask(63));
    JournalTransactionArbiter arbiter;
    JournalStorageOwner owner(core, arbiter);
    AsyncCommandJournal journal(owner, arbiter);
    storage.cutAfter = afterWrite;
    storage.cutBefore = !afterWrite;
    assert(!journal.BeginScoped(1, Id, 2, 77));
    assert(owner.RunOne());
    assert(!journal.BeginScoped(1, Id, 2, 77));
    assert(journal.LastStorageState() == CommandJournal::StorageState::Unknown);
    assert(!journal.CanBeginOperation() && !core.IsHealthy());
    PersistentCommandJournal reopened(storage);
    assert(reopened.IsHealthy() && reopened.HasPending() == afterWrite);
    assert(!reopened.WasTimedPresentationCompletedHistorical(1, Id, 2, 77));
  }
}
} // namespace
int main() {
  ExactResultAndOriginalEpoch();
  CancelledSuccessIsAdopted();
  ReservationCancellationAndRecoveryFence();
  LostAckIsNeverReplayedAsSuccess();
}

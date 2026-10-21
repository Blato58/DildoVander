#include <algorithm>
#include <cassert>
#include <chrono>
#include <condition_variable>
#include <cstring>
#include <future>
#include <mutex>
#include <thread>
#include <vector>

#include "../src/features/device_control/JournalStorageOwner.h"
#include "../src/features/device_control/PersistentCommandJournal.h"

namespace {
using Owner = JournalStorageOwner;
using Purpose = JournalTransactionArbiter::Purpose;
using Token = JournalTransactionArbiter::Token;
constexpr char Id[] = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
class Storage final : public JournalStorage {
 public:
  JournalReadResult Read(uint8_t* output, size_t capacity, size_t& length) override {
    length = 0;
    if (bytes.empty()) return JournalReadResult::Missing;
    if (bytes.size() > capacity) return JournalReadResult::Failed;
    std::copy(bytes.begin(), bytes.end(), output); length = bytes.size();
    return JournalReadResult::Found;
  }
  bool Write(const uint8_t* input, size_t length) override {
    ++writes;
    if (blocked) {
      std::unique_lock<std::mutex> lock(mutex);
      entered = true; changed.notify_all();
      changed.wait(lock, [&]() { return released; });
    }
    if (cutBefore) return false;
    bytes.assign(input, input + length);
    return !cutAfter;
  }
  void AwaitWrite() {
    std::unique_lock<std::mutex> lock(mutex);
    assert(changed.wait_for(lock, std::chrono::seconds(3), [&]() { return entered; }));
  }
  void ReleaseWrite() {
    std::lock_guard<std::mutex> lock(mutex);
    released = true; changed.notify_all();
  }
  std::vector<uint8_t> bytes;
  size_t writes = 0;
  bool blocked = false, cutBefore = false, cutAfter = false;
 private:
  std::mutex mutex;
  std::condition_variable changed;
  bool entered = false, released = false;
};
Owner::Request Request(Owner::Operation operation) {
  Owner::Request request;
  request.operation = operation; request.epoch = 1; request.slot = 2;
  request.fingerprint = 77;
  std::copy(Id, Id + sizeof(Id), request.id.begin());
  return request;
}
void Initialize(PersistentCommandJournal& journal) {
  assert(journal.EstablishScopedIdentity("device-001", true));
  assert(journal.PersistReadyMask(0x3f));
}
Owner::Completion Complete(Owner& owner, const Token& token) {
  assert(owner.RunOne());
  Owner::Completion result;
  assert(owner.TryReadCompletion(token, result));
  return result;
}
void QueueAndResultCapacity() {
  Storage storage; PersistentCommandJournal journal(storage); Initialize(journal);
  JournalTransactionArbiter arbiter; Owner owner(journal, arbiter);
  Token token, extra;
  const auto writes = storage.writes;
  assert(owner.TrySubmit(Request(Owner::Operation::Begin), Purpose::Admission, token));
  assert(storage.writes == writes && !arbiter.TryEnter());
  assert(!owner.TrySubmit(Request(Owner::Operation::Begin), Purpose::Admission, extra));
  auto result = Complete(owner, token);
  assert(result.status == Owner::Status::Succeeded && result.current);
  assert(result.snapshot.pendingSlot == 2);
  assert(!owner.TrySubmit(Request(Owner::Operation::Begin), Purpose::Admission, extra));
  assert(!arbiter.TryEnter()); // durable result still owns reserved publication capacity
  assert(owner.ReleaseCompletion(token));
  assert(arbiter.TryEnter()); arbiter.Leave();
  assert(!owner.ReleaseCompletion(token));
}
void StopBeforeAndAfterWrite() {
  for (bool before : {false, true}) {
    Storage storage; PersistentCommandJournal journal(storage); Initialize(journal);
    JournalTransactionArbiter arbiter; Owner owner(journal, arbiter);
    Token token;
    const auto writes = storage.writes;
    assert(owner.TrySubmit(Request(Owner::Operation::Begin), Purpose::Admission, token));
    if (before) owner.CancelAdmissions();
    auto result = Complete(owner, token);
    if (!before) owner.CancelAdmissions();
    assert(owner.TryReadCompletion(token, result) && !result.current);
    assert(result.status == (before ? Owner::Status::CancelledBeforeWrite : Owner::Status::Succeeded));
    assert(storage.writes == writes + (before ? 0 : 1));
    assert(result.snapshot.pendingSlot == (before ? 0 : 2));
    assert(!arbiter.TryEnter());
    assert(owner.ReleaseCompletion(token));
    Token refused;
    assert(!owner.TrySubmit(Request(Owner::Operation::Begin), Purpose::Admission, refused));
    assert(arbiter.TryEnter()); arbiter.Leave();
  }
}
void StopDuringWriteDoesNotWait() {
  Storage storage; PersistentCommandJournal journal(storage); Initialize(journal);
  JournalTransactionArbiter arbiter; Owner owner(journal, arbiter);
  Token token;
  assert(owner.TrySubmit(Request(Owner::Operation::Reserve), Purpose::Admission, token));
  storage.blocked = true;
  std::thread worker([&]() { assert(owner.RunOne()); });
  storage.AwaitWrite();
  auto stop = std::async(std::launch::async, [&]() { owner.CancelAdmissions(); });
  const bool immediate = stop.wait_for(std::chrono::seconds(1)) == std::future_status::ready;
  // Always release the simulated stalled device, even on a regression.
  storage.ReleaseWrite(); worker.join(); stop.wait();
  assert(immediate && arbiter.AdmissionsStopped());
  Owner::Completion result;
  assert(owner.TryReadCompletion(token, result));
  assert(result.status == Owner::Status::Succeeded && !result.current);
  assert(!result.snapshot.HasPresentationReservation());
  JournalSnapshotView view(result.snapshot);
  assert(view.FindHistorical(1, Id, 2, 77) == CommandJournal::Lookup::Same);
  assert(!view.WasTimedPresentationCompletedHistorical(1, Id, 2, 77));
  assert(result.snapshot.history[0].evidencePinned);
  assert(owner.ReleaseCompletion(token));
}
void IdleStopBurnsReservedAuthority() {
  Storage storage; PersistentCommandJournal journal(storage); Initialize(journal);
  JournalTransactionArbiter arbiter; Owner owner(journal, arbiter);
  Token token;
  assert(owner.TrySubmit(Request(Owner::Operation::Reserve), Purpose::Admission, token));
  auto reserved = Complete(owner, token);
  assert(reserved.snapshot.HasPresentationReservation());
  assert(owner.ReleaseCompletion(token));
  owner.CancelAdmissions();
  assert(arbiter.ResumeAfterRecovery(arbiter.Cancellation()));
  // A new request cannot reconstruct admission from the committed tombstone.
  assert(owner.TrySubmit(Request(Owner::Operation::BeginReserved), Purpose::Admission, token));
  auto result = Complete(owner, token);
  assert(result.status == Owner::Status::Refused && !result.snapshot.HasPresentationReservation());
  assert(!result.snapshot.pendingSlot);
  assert(owner.ReleaseCompletion(token));
}
void LostAcknowledgementRequiresReopen() {
  for (bool after : {false, true}) {
    Storage storage; PersistentCommandJournal journal(storage); Initialize(journal);
    JournalTransactionArbiter arbiter; Owner owner(journal, arbiter);
    storage.cutBefore = !after; storage.cutAfter = after;
    Token token;
    assert(owner.TrySubmit(Request(Owner::Operation::Begin), Purpose::Admission, token));
    auto result = Complete(owner, token);
    assert(result.status == Owner::Status::Unknown && !result.snapshot.healthy);
    assert(owner.ReleaseCompletion(token));
    assert(!owner.TrySubmit(Request(Owner::Operation::Begin), Purpose::Admission, token));
    storage.cutBefore = false; storage.cutAfter = false;
    PersistentCommandJournal reopened(storage);
    assert(reopened.IsHealthy());
    assert(reopened.FindHistorical(1, Id, 2, 77) ==
        (after ? CommandJournal::Lookup::Same : CommandJournal::Lookup::Missing));
    assert(reopened.HasPending() == after);
  }
}
void EvidenceFinishesAfterStop() {
  Storage storage; PersistentCommandJournal journal(storage); Initialize(journal);
  assert(journal.BeginScoped(1, Id, 2, 77));
  JournalTransactionArbiter arbiter; Owner owner(journal, arbiter);
  owner.CancelAdmissions();
  auto request = Request(Owner::Operation::ResolveRestored);
  Token token;
  assert(owner.TrySubmit(request, Purpose::Evidence, token));
  auto result = Complete(owner, token);
  assert(result.status == Owner::Status::Succeeded && !result.snapshot.pendingSlot);
  assert(arbiter.AdmissionsStopped());
  assert(owner.ReleaseCompletion(token));
}
}
int main() {
  QueueAndResultCapacity(); StopBeforeAndAfterWrite(); StopDuringWriteDoesNotWait();
  IdleStopBurnsReservedAuthority(); LostAcknowledgementRequiresReopen(); EvidenceFinishesAfterStop();
}

#include <assert.h>
#include <stddef.h>
#include <type_traits>

#include <algorithm>
#include <array>
#include <string>

#include "../src/features/device_control/JournalStorage.h"
#include "../src/features/device_control/PersistentCommandJournal.h"

namespace {

constexpr char FirstId[] = "523ef590-ce4d-4901-91b3-79de1bfe7e35";
constexpr char SecondId[] = "7a2fef87-0a80-4115-86f3-3a93ad984b82";
constexpr uint64_t FirstFingerprint = 0x123456789abcdef0ULL;

class MemoryStorage final : public JournalStorage {
 public:
  JournalReadResult Read(uint8_t* output, size_t capacity, size_t& length) override {
    if (failRead) return JournalReadResult::Failed;
    if (!present) return JournalReadResult::Missing;
    if (storedLength > capacity) return JournalReadResult::Failed;
    std::copy(data.begin(), data.begin() + storedLength, output);
    length = storedLength;
    return JournalReadResult::Found;
  }
  bool Write(const uint8_t* input, size_t length) override {
    if (failWrite || length > data.size()) return false;
    std::copy(input, input + length, data.begin());
    storedLength = length;
    present = true;
    return true;
  }

  std::array<uint8_t, PersistentCommandJournal::RecordSize> data{};
  size_t storedLength = 0;
  bool present = false;
  bool failRead = false;
  bool failWrite = false;
};

void CompareQueries(const PersistentCommandJournal& journal) {
  JournalSnapshot snapshot;
  assert(journal.ExportSnapshot(snapshot));
  JournalSnapshotView view(snapshot);
  assert(view.HasPending() == journal.HasPending());
  assert(view.PendingCompartment() == journal.PendingCompartment());
  assert(view.ReadyMask() == journal.ReadyMask());
  assert(view.CurrentEpoch() == journal.CurrentEpoch());
  assert(view.CanBeginOperation() == journal.CanBeginOperation());
  assert(view.HasPresentationReservation() == journal.HasPresentationReservation());
  assert(view.HasPresentationReservation(1, FirstId, 2, FirstFingerprint) ==
         journal.HasPresentationReservation(1, FirstId, 2, FirstFingerprint));
  assert(view.HasPresentationReservation(2, SecondId, 4, FirstFingerprint) ==
         journal.HasPresentationReservation(2, SecondId, 4, FirstFingerprint));
  for (size_t index = 0; index < snapshot.historyCount; ++index) {
    JournalHistoryEntry source;
    assert(journal.ReadHistoryEntry(index, source));
    const auto& copied = snapshot.history[index];
    assert(source.identity.epoch == copied.epoch);
    assert(source.identity.operationId == copied.id);
    assert(source.identity.compartment == copied.slot);
    assert(source.identity.fingerprint == copied.fingerprint);
    assert(source.timedCompleted == copied.timedCompleted);
    assert(source.evidencePinned == copied.evidencePinned);
  }

  for (const std::string id : {FirstId, SecondId, "523EF590-CE4D-4901-91B3-79DE1BFE7E35"}) {
    for (uint8_t slot = 1; slot <= 6; ++slot) {
      for (uint64_t fingerprint : std::array<uint64_t, 2>{0, FirstFingerprint}) {
        assert(view.Find(id, slot, fingerprint) == journal.Find(id, slot, fingerprint));
        assert(view.FindScoped(1, id, slot, fingerprint) == journal.FindScoped(1, id, slot, fingerprint));
        for (uint64_t epoch = 0; epoch <= journal.CurrentEpoch(); ++epoch) {
          assert(view.FindHistorical(epoch, id, slot, fingerprint) ==
                 journal.FindHistorical(epoch, id, slot, fingerprint));
          assert(view.WasTimedPresentationCompletedHistorical(epoch, id, slot, fingerprint) ==
                 journal.WasTimedPresentationCompletedHistorical(epoch, id, slot, fingerprint));
        }
        assert(view.WasTimedPresentationCompletedScoped(1, id, slot, fingerprint) ==
               journal.WasTimedPresentationCompletedScoped(1, id, slot, fingerprint));
      }
      assert(view.WasTimedPresentationCompleted(id, slot) ==
             journal.WasTimedPresentationCompleted(id, slot));
      assert(view.WasLegacyTimedPresentationCompleted(id, slot) ==
             journal.WasLegacyTimedPresentationCompleted(id, slot));
    }
  }

  const auto lookup = view.FindHistorical(snapshot.currentEpoch, FirstId, 1, FirstFingerprint);
  (void)lookup;
  assert(!view.Begin(FirstId, 1));
  assert(!view.BeginWithIdentity(FirstId, 1, FirstFingerprint));
  assert(!view.BeginScoped(snapshot.currentEpoch, FirstId, 1, FirstFingerprint));
  assert(!view.ReservePresentation(snapshot.currentEpoch, FirstId, 1, FirstFingerprint));
  assert(!view.BeginReservedPresentation(snapshot.currentEpoch, FirstId, 1, FirstFingerprint));
  assert(!view.Resolve(0));
  assert(!view.ResolveRestored(1));
  assert(!view.ConfirmTimedPresentation(FirstId, 1, FirstFingerprint));
  assert(!view.ConfirmTimedPresentationScoped(snapshot.currentEpoch, FirstId, 1, FirstFingerprint));
  assert(!view.PersistReadyMask(0));
}

void TestLegacyAndRestoredQueries() {
  MemoryStorage storage;
  PersistentCommandJournal journal(storage);
  CompareQueries(journal);  // Healthy, missing storage origin.
  assert(journal.PersistReadyMask(0x3f));
  assert(journal.BeginWithIdentity(FirstId, 2, FirstFingerprint));
  CompareQueries(journal);  // Pending legacy record.
  assert(journal.ResolveRestored(2));
  assert(journal.ConfirmTimedPresentation(FirstId, 2, FirstFingerprint));
  CompareQueries(journal);  // Restored and timed-completed history.
}

void TestScopedReservationAndCompactionQueries() {
  MemoryStorage storage;
  PersistentCommandJournal journal(storage);
  assert(journal.EstablishScopedIdentity("device_01", true));
  assert(journal.PersistReadyMask(0x3f));
  assert(journal.BeginScoped(1, FirstId, 3, FirstFingerprint));
  CompareQueries(journal);  // Pending scoped record.
  assert(journal.ResolveRestored(3));
  assert(journal.ConfirmTimedPresentationScoped(1, FirstId, 3, FirstFingerprint));
  CompareQueries(journal);
  assert(journal.ReleaseEvidencePin(1, FirstId, 3, FirstFingerprint));
  assert(journal.AdvanceEpochAndCompact());
  CompareQueries(journal);  // Released history was compacted.

  assert(journal.ReservePresentation(2, SecondId, 4, FirstFingerprint));
  CompareQueries(journal);  // Volatile reservation tuple is published read-only.
  JournalSnapshot reserved{};
  assert(journal.ExportSnapshot(reserved));
  assert(reserved.reservation.epoch == 2);
  assert(std::string(reserved.reservation.id) == SecondId);
  assert(journal.BeginReservedPresentation(2, SecondId, 4, FirstFingerprint));
  CompareQueries(journal);  // Reservation consumed, pending is visible.
}

void TestUnhealthyExportIsZeroedAndCopyable() {
  static_assert(std::is_trivially_copyable<JournalSnapshot>::value, "snapshot must be fixed-copy safe");
  MemoryStorage storage;
  storage.failRead = true;
  PersistentCommandJournal journal(storage);
  JournalSnapshot snapshot;
  snapshot.readyMask = 0xff;
  assert(!journal.ExportSnapshot(snapshot));
  assert(!snapshot.healthy);
  assert(snapshot.origin == JournalSnapshot::Origin::Unavailable);
  assert(snapshot.readyMask == 0 && snapshot.historyCount == 0 && snapshot.pendingId[0] == '\0');
  JournalSnapshot copied = snapshot;
  JournalSnapshotView view(copied);
  assert(view.Find(FirstId, 1, 0) == CommandJournal::Lookup::Unavailable);
  assert(!view.CanBeginOperation());
  assert(!view.HasPresentationReservation());
  assert(!view.Begin(FirstId, 1));
}

}  // namespace

int main() {
  TestLegacyAndRestoredQueries();
  TestScopedReservationAndCompactionQueries();
  TestUnhealthyExportIsZeroedAndCopyable();
  return 0;
}

#include <assert.h>

#include <algorithm>
#include <array>
#include <functional>
#include <cstdio>
#include <string>
#include <type_traits>

#include "../src/features/device_control/PersistentCommandJournal.h"

namespace {

using Journal = PersistentCommandJournal;
using Lookup = CommandJournal::Lookup;
static_assert(!std::is_copy_constructible<Journal>::value, "Reservation authority must not be copied");
static_assert(!std::is_move_constructible<Journal>::value, "Reservation authority must not be moved");
static_assert(!std::is_copy_assignable<Journal>::value, "Reservation authority must not be copy-assigned");
static_assert(!std::is_move_assignable<Journal>::value, "Reservation authority must not be move-assigned");
constexpr char FirstId[] = "523ef590-ce4d-4901-91b3-79de1bfe7e35";
constexpr char UpperId[] = "523EF590-CE4D-4901-91B3-79DE1BFE7E35";
constexpr char SecondId[] = "7a2fef87-0a80-4115-86f3-3a93ad984b82";
constexpr uint64_t Fingerprint = 51;

// Atomic replacement cuts model old/new committed bytes with a failed ACK;
// these host tests do not claim physical NVS or actuator acceptance.
class Storage final : public JournalStorage {
 public:
  enum class Cut { None, Before, After };

  JournalReadResult Read(uint8_t* output, size_t capacity, size_t& length) override {
    if (failRead || (present && size > capacity)) return JournalReadResult::Failed;
    if (!present) return JournalReadResult::Missing;
    length = size;
    std::copy(bytes.begin(), bytes.begin() + size, output);
    return JournalReadResult::Found;
  }

  bool Write(const uint8_t* data, size_t length) override {
    ++writes;
    if (onWrite) onWrite();
    if (cut == Cut::Before) return false;
    assert(length == bytes.size());
    std::copy(data, data + length, bytes.begin());
    present = true;
    size = length;
    return cut != Cut::After;
  }

  std::array<uint8_t, Journal::RecordSize> bytes{};
  size_t size = 0;
  size_t writes = 0;
  bool present = false;
  bool failRead = false;
  Cut cut = Cut::None;
  std::function<void()> onWrite;
};

void Provision(Journal& journal) {
  assert(journal.EstablishScopedIdentity("reservation-device", true));
  assert(journal.PersistReadyMask(0x3f));
}

void AssertTombstone(const Journal& journal, const std::string& id = FirstId,
                     uint8_t compartment = 2, uint64_t fingerprint = Fingerprint) {
  assert(!journal.HasPending());
  assert(journal.PendingCompartment() == 0);
  assert(journal.FindHistorical(1, id, compartment, fingerprint) == Lookup::Same);
  assert(journal.IsEvidencePinned(1, id, compartment, fingerprint));
  assert(!journal.WasTimedPresentationCompletedScoped(1, id, compartment, fingerprint));
  assert(!journal.WasTimedPresentationCompletedHistorical(1, id, compartment, fingerprint));
}

void TestReserveThenConsumeAndProveOnlyAfterReturn() {
  Storage storage;
  Journal journal(storage);
  Provision(journal);
  const size_t before = storage.writes;
  storage.onWrite = [&]() {
    assert(!journal.HasPresentationReservation());
    assert(!journal.HasPending());
  };
  assert(journal.ReservePresentation(1, FirstId, 2, Fingerprint));
  storage.onWrite = {};
  assert(storage.writes == before + 1);
  assert(storage.size == Journal::RecordSize && storage.bytes[4] == 4);
  assert(journal.HasPresentationReservation());
  assert(journal.HasPresentationReservation(1, FirstId, 2, Fingerprint));
  assert(journal.HasPresentationReservation(1, UpperId, 2, Fingerprint));
  AssertTombstone(journal);
  assert(journal.ReadyMask() == 0x3f);
  assert(!journal.CanBeginOperation());
  assert(!journal.ConfirmTimedPresentationScoped(1, FirstId, 2, Fingerprint));

  storage.onWrite = [&]() {
    assert(!journal.HasPresentationReservation());
    assert(!journal.HasPending());
  };
  assert(journal.BeginReservedPresentation(1, FirstId, 2, Fingerprint));
  storage.onWrite = {};
  assert(!journal.HasPresentationReservation());
  assert(journal.HasPending() && journal.PendingCompartment() == 2);
  assert(journal.HistoryCount() == 1);
  assert(!journal.BeginReservedPresentation(1, FirstId, 2, Fingerprint));
  assert(!journal.ConfirmTimedPresentationScoped(1, FirstId, 2, Fingerprint));
  assert(journal.ResolveRestored(2));
  // The physical/control owner alone calls this trusted API after its actual
  // completed return flow. Reservation/reopen by itself never creates proof.
  assert(journal.ConfirmTimedPresentationScoped(1, FirstId, 2, Fingerprint));
  assert(journal.WasTimedPresentationCompletedScoped(1, FirstId, 2, Fingerprint));
}

void TestWrongFieldsAndIdempotentRetry() {
  Storage storage;
  Journal journal(storage);
  Provision(journal);
  assert(!journal.ReservePresentation(0, FirstId, 2, Fingerprint));
  assert(!journal.ReservePresentation(2, FirstId, 2, Fingerprint));
  assert(!journal.ReservePresentation(1, "invalid", 2, Fingerprint));
  assert(!journal.ReservePresentation(1, FirstId, 0, Fingerprint));
  assert(!journal.ReservePresentation(1, FirstId, 7, Fingerprint));
  assert(!journal.ReservePresentation(1, FirstId, 2, 0));
  assert(journal.PersistReadyMask(1));
  assert(!journal.ReservePresentation(1, FirstId, 2, Fingerprint));
  assert(journal.PersistReadyMask(0x3f));
  assert(journal.ReservePresentation(1, FirstId, 2, Fingerprint));
  const size_t reservedWrites = storage.writes;
  assert(journal.ReservePresentation(1, UpperId, 2, Fingerprint));
  assert(!journal.ReservePresentation(1, SecondId, 2, Fingerprint));
  assert(!journal.ReservePresentation(1, FirstId, 3, Fingerprint));
  assert(!journal.ReservePresentation(1, FirstId, 2, Fingerprint + 1));
  assert(!journal.BeginReservedPresentation(0, FirstId, 2, Fingerprint));
  assert(!journal.BeginReservedPresentation(2, FirstId, 2, Fingerprint));
  assert(!journal.BeginReservedPresentation(1, SecondId, 2, Fingerprint));
  assert(!journal.BeginReservedPresentation(1, FirstId, 3, Fingerprint));
  assert(!journal.BeginReservedPresentation(1, FirstId, 2, Fingerprint + 1));
  assert(journal.HasPresentationReservation(1, FirstId, 2, Fingerprint));
  assert(storage.writes == reservedWrites);
  assert(journal.BeginReservedPresentation(1, UpperId, 2, Fingerprint));
  assert(!journal.ReservePresentation(1, SecondId, 3, Fingerprint));
}

void TestCancelAndRestartLeaveOnlyTombstone() {
  Storage storage;
  Journal journal(storage);
  Provision(journal);
  assert(journal.ReservePresentation(1, FirstId, 2, Fingerprint));
  const auto reservedBytes = storage.bytes;
  const size_t reservedWrites = storage.writes;
  journal.CancelPresentationReservation();
  journal.CancelPresentationReservation();
  assert(!journal.HasPresentationReservation());
  assert(storage.bytes == reservedBytes && storage.writes == reservedWrites);
  AssertTombstone(journal);
  assert(!journal.ReservePresentation(1, FirstId, 2, Fingerprint));
  assert(!journal.BeginReservedPresentation(1, FirstId, 2, Fingerprint));
  assert(!journal.BeginScoped(1, FirstId, 2, Fingerprint));
  assert(journal.ReservePresentation(1, SecondId, 3, Fingerprint + 1));

  Journal reopened(storage);
  assert(reopened.IsHealthy() && !reopened.HasPresentationReservation());
  AssertTombstone(reopened);
  AssertTombstone(reopened, SecondId, 3, Fingerprint + 1);
  assert(!reopened.ReservePresentation(1, SecondId, 3, Fingerprint + 1));
  assert(!reopened.BeginReservedPresentation(1, SecondId, 3, Fingerprint + 1));
  assert(!reopened.BeginScoped(1, SecondId, 3, Fingerprint + 1));
}

void TestOtherMutatorsCannotCrossReservation() {
  Storage storage;
  Journal journal(storage);
  Provision(journal);
  assert(journal.ReservePresentation(1, FirstId, 2, Fingerprint));
  const auto reservedBytes = storage.bytes;
  const size_t reservedWrites = storage.writes;
  std::array<JournalHistoryEntry, Journal::HistoryCapacity> history{};
  assert(journal.ReadHistoryEntry(0, history[0]));
  std::array<bool, Journal::HistoryCapacity> referenced{};
  assert(!journal.Begin(SecondId, 3));
  assert(!journal.BeginWithIdentity(SecondId, 3, Fingerprint));
  assert(!journal.BeginScoped(1, SecondId, 3, Fingerprint));
  assert(!journal.PersistReadyMask(0));
  assert(!journal.Resolve(0x3d));
  assert(!journal.ResolveRestored(2));
  assert(!journal.EstablishScopedIdentity("other-device", true));
  std::array<uint8_t, Journal::RecordSize> preview{};
  assert(!journal.PreviewScopedIdentity("other-device", true, preview));
  assert(!journal.AdvanceEpochAndCompact());
  assert(!journal.ReleaseEvidencePin(1, FirstId, 2, Fingerprint));
  assert(!journal.RetainEvidencePin(1, FirstId, 2, Fingerprint));
  assert(!journal.CommitEvidenceReconciliation(1, history, 1, referenced, false));
  assert(!journal.CommitEvidenceReconciliation(1, history, 1, referenced, true));
  assert(!journal.ConfirmTimedPresentationScoped(1, FirstId, 2, Fingerprint));
  assert(storage.bytes == reservedBytes && storage.writes == reservedWrites);
  assert(journal.HasPresentationReservation(1, FirstId, 2, Fingerprint));
}

void TestReservationCanConsumeLastHistoryEntry() {
  Storage storage;
  Journal journal(storage);
  Provision(journal);
  for (size_t index = 0; index < Journal::HistoryCapacity - 1; ++index) {
    std::string id = FirstId;
    id[0] = "0123456789abcdef"[index / 16];
    id[1] = "0123456789abcdef"[index % 16];
    assert(journal.BeginScoped(1, id, 1, index + 1));
    assert(journal.ResolveRestored(1));
  }
  assert(journal.ReservePresentation(1, FirstId, 2, Fingerprint));
  assert(journal.HistoryCount() == Journal::HistoryCapacity);
  assert(!journal.CanBeginOperation());
  const size_t reservedWrites = storage.writes;
  assert(journal.ReservePresentation(1, FirstId, 2, Fingerprint));
  assert(storage.writes == reservedWrites);
  assert(journal.BeginReservedPresentation(1, FirstId, 2, Fingerprint));
  assert(journal.HasPending() && journal.PendingCompartment() == 2);
  assert(journal.HistoryCount() == Journal::HistoryCapacity);
  assert(journal.ResolveRestored(2));
  assert(!journal.ReservePresentation(1, SecondId, 3, Fingerprint));
}

void TestReserveWriteFailureRequiresAuthoritativeReopen() {
  for (const auto cut : {Storage::Cut::Before, Storage::Cut::After}) {
    Storage storage;
    Journal journal(storage);
    Provision(journal);
    storage.cut = cut;
    assert(!journal.ReservePresentation(1, FirstId, 2, Fingerprint));
    assert(!journal.IsHealthy() && !journal.HasPresentationReservation());
    assert(!journal.ReservePresentation(1, FirstId, 2, Fingerprint));
    assert(!journal.BeginReservedPresentation(1, FirstId, 2, Fingerprint));
    storage.cut = Storage::Cut::None;
    Journal reopened(storage);
    assert(reopened.IsHealthy() && !reopened.HasPresentationReservation());
    assert(!reopened.HasPending());
    assert(!reopened.BeginReservedPresentation(1, FirstId, 2, Fingerprint));
    if (cut == Storage::Cut::Before) {
      assert(reopened.FindHistorical(1, FirstId, 2, Fingerprint) == Lookup::Missing);
      assert(reopened.ReservePresentation(1, FirstId, 2, Fingerprint));
    } else {
      AssertTombstone(reopened);
      assert(!reopened.ReservePresentation(1, FirstId, 2, Fingerprint));
      assert(!reopened.BeginScoped(1, FirstId, 2, Fingerprint));
    }
  }
}

void TestConsumeWriteFailureNeverRestoresCapability() {
  for (const auto cut : {Storage::Cut::Before, Storage::Cut::After}) {
    Storage storage;
    Journal journal(storage);
    Provision(journal);
    assert(journal.ReservePresentation(1, FirstId, 2, Fingerprint));
    storage.cut = cut;
    assert(!journal.BeginReservedPresentation(1, FirstId, 2, Fingerprint));
    assert(!journal.IsHealthy() && !journal.HasPresentationReservation());
    assert(!journal.BeginReservedPresentation(1, FirstId, 2, Fingerprint));
    storage.cut = Storage::Cut::None;
    Journal reopened(storage);
    assert(reopened.IsHealthy() && !reopened.HasPresentationReservation());
    assert(!reopened.ReservePresentation(1, FirstId, 2, Fingerprint));
    assert(!reopened.BeginReservedPresentation(1, FirstId, 2, Fingerprint));
    assert(!reopened.BeginScoped(1, FirstId, 2, Fingerprint));
    assert(!reopened.WasTimedPresentationCompletedScoped(1, FirstId, 2, Fingerprint));
    assert(reopened.IsEvidencePinned(1, FirstId, 2, Fingerprint));
    assert(reopened.HasPending() == (cut == Storage::Cut::After));
    assert(reopened.PendingCompartment() == (cut == Storage::Cut::After ? 2 : 0));
    assert(reopened.HistoryCount() == 1);
  }
}

void TestLegacyAndUnavailableCannotReserve() {
  Storage storage;
  Journal legacy(storage);
  assert(legacy.PersistReadyMask(0x3f));
  assert(!legacy.ReservePresentation(0, FirstId, 2, Fingerprint));
  assert(!legacy.ReservePresentation(1, FirstId, 2, Fingerprint));
  assert(!legacy.BeginReservedPresentation(0, FirstId, 2, Fingerprint));
  assert(legacy.BeginWithIdentity(FirstId, 2, Fingerprint));
  assert(legacy.ResolveRestored(2));
  storage.failRead = true;
  Journal unavailable(storage);
  assert(!unavailable.ReservePresentation(1, FirstId, 2, Fingerprint));
  assert(!unavailable.HasPresentationReservation());
}

void UpdateCrc(std::array<uint8_t, Journal::RecordSize>& bytes) {
  uint32_t crc = 0xffffffff;
  for (size_t index = 0; index < bytes.size() - 4; ++index) {
    crc ^= bytes[index];
    for (unsigned bit = 0; bit < 8; ++bit) crc = (crc >> 1) ^ ((crc & 1) ? 0xedb88320 : 0);
  }
  crc = ~crc;
  for (unsigned byte = 0; byte < 4; ++byte) bytes[bytes.size() - 4 + byte] = static_cast<uint8_t>(crc >> (8 * byte));
}
void TestTechnicalMarkerFormatAndCompactedPins() {
  Storage production, technical;
  Journal productionJournal(production), technicalJournal(technical);
  Provision(productionJournal); Provision(technicalJournal);
  assert(productionJournal.ReservePresentation(1, FirstId, 2, Fingerprint));
  assert(technicalJournal.ReserveTechnicalPresentation(1, FirstId, 2, Fingerprint));
  assert(production.bytes[4] == 4 && technical.bytes[4] == 5);
  auto expected = production.bytes;
  expected[4] = 5; expected[118 + 37] |= 4; UpdateCrc(expected);
  assert(expected == technical.bytes); // all existing v4 fields/bytes retain their positions
  const auto committed = technical.bytes;
  technical.bytes[118 + 37] ^= 4; // removing origin without updating CRC must never relabel a row
  Journal corrupt(technical);
  assert(!corrupt.IsHealthy());
  technical.bytes = committed;
  technical.bytes[4] = 4; UpdateCrc(technical.bytes);
  Journal unsafeDowngrade(technical);
  assert(!unsafeDowngrade.IsHealthy()); // v4 refuses the v5-only flag, even with a valid CRC
  technical.bytes = committed;
  assert(technicalJournal.BeginReservedPresentation(1, FirstId, 2, Fingerprint));
  assert(technicalJournal.ResolveRestored(2));
  assert(technicalJournal.ConfirmTimedPresentationScoped(1, FirstId, 2, Fingerprint));
  for (unsigned ordinal = 1; ordinal != Journal::HistoryCapacity; ++ordinal) {
    std::string id = SecondId;
    const char* hex = "0123456789abcdef";
    id[34] = hex[ordinal >> 4]; id[35] = hex[ordinal & 15];
    assert(technicalJournal.ReservePresentation(1, id, 2, 100 + ordinal));
    assert(technicalJournal.BeginReservedPresentation(1, id, 2, 100 + ordinal));
    assert(technicalJournal.ResolveRestored(2));
    assert(technicalJournal.ConfirmTimedPresentationScoped(1, id, 2, 100 + ordinal));
  }
  std::array<JournalHistoryEntry, Journal::HistoryCapacity> history{};
  const size_t count = technicalJournal.HistoryCount();
  assert(count == history.size());
  for (size_t index = 0; index != count; ++index) assert(technicalJournal.ReadHistoryEntry(index, history[index]));
  std::array<bool, Journal::HistoryCapacity> retained{};
  retained[0] = retained[1] = true;
  assert(history[0].technicalTest && !history[1].technicalTest);
  auto forged = history; forged[0].technicalTest = false;
  assert(!technicalJournal.CommitEvidenceReconciliation(1, forged, count, retained, true));
  assert(technicalJournal.CommitEvidenceReconciliation(1, history, count, retained, true));
  assert(technicalJournal.CurrentEpoch() == 2 && technical.bytes[4] == 5);
  Journal reopened(technical);
  assert(reopened.IsHealthy() && reopened.HistoryCount() == 2);
  assert(reopened.IsTechnicalTestOperation(1, FirstId, 2, Fingerprint));
  assert(reopened.WasTimedPresentationCompletedHistorical(1, FirstId, 2, Fingerprint));
  assert(reopened.IsEvidencePinned(1, FirstId, 2, Fingerprint));
  assert(!reopened.IsTechnicalTestOperation(2, FirstId, 2, Fingerprint));
  assert(!reopened.IsTechnicalTestOperation(history[1].identity.epoch, history[1].identity.operationId,
      history[1].identity.compartment, history[1].identity.fingerprint));
  assert(!reopened.BeginReservedPresentation(1, FirstId, 2, Fingerprint));
}

}  // namespace

void TestTechnicalOriginSurvivesAllPhysicalBoundaries() {
  Storage storage;
  Journal journal(storage);
  Provision(journal);
  assert(journal.ReserveTechnicalPresentation(1, FirstId, 2, Fingerprint));
  assert(storage.bytes[4] == 5);
  assert(journal.IsTechnicalTestOperation(1, UpperId, 2, Fingerprint));
  assert(!journal.ReservePresentation(1, FirstId, 2, Fingerprint));
  assert(journal.ReserveTechnicalPresentation(1, FirstId, 2, Fingerprint));
  assert(journal.BeginReservedPresentation(1, FirstId, 2, Fingerprint));
  Journal pending(storage);
  assert(pending.HasPending() && pending.IsTechnicalTestOperation(1, FirstId, 2, Fingerprint));
  assert(!pending.HasPresentationReservation());
  assert(pending.ResolveRestored(2));
  assert(pending.ConfirmTimedPresentationScoped(1, FirstId, 2, Fingerprint));
  Journal complete(storage);
  assert(complete.IsTechnicalTestOperation(1, FirstId, 2, Fingerprint));
  assert(complete.WasTimedPresentationCompletedHistorical(1, FirstId, 2, Fingerprint));
  JournalSnapshot snapshot;
  assert(complete.ExportSnapshot(snapshot));
  assert(JournalSnapshotView(snapshot).IsTechnicalTestOperation(1, FirstId, 2, Fingerprint));
  assert(!complete.IsTechnicalTestOperation(1, FirstId, 3, Fingerprint));
  assert(!complete.IsTechnicalTestOperation(2, FirstId, 2, Fingerprint));
  assert(!complete.IsTechnicalTestOperation(1, FirstId, 2, Fingerprint + 1));
  for (const auto cut : {Storage::Cut::Before, Storage::Cut::After}) {
    Storage failing;
    Journal original(failing);
    Provision(original);
    failing.cut = cut;
    assert(!original.ReserveTechnicalPresentation(1, FirstId, 2, Fingerprint));
    assert(!original.IsHealthy());
    failing.cut = Storage::Cut::None;
    Journal reopened(failing);
    assert(reopened.IsHealthy() && !reopened.HasPresentationReservation());
    assert(reopened.IsTechnicalTestOperation(1, FirstId, 2, Fingerprint) == (cut == Storage::Cut::After));
    assert(!reopened.BeginReservedPresentation(1, FirstId, 2, Fingerprint));
  }
}

int main() {
  TestTechnicalMarkerFormatAndCompactedPins();
  TestTechnicalOriginSurvivesAllPhysicalBoundaries();
  TestReserveThenConsumeAndProveOnlyAfterReturn();
  TestWrongFieldsAndIdempotentRetry();
  TestCancelAndRestartLeaveOnlyTombstone();
  TestOtherMutatorsCannotCrossReservation();
  TestReservationCanConsumeLastHistoryEntry();
  TestReserveWriteFailureRequiresAuthoritativeReopen();
  TestConsumeWriteFailureNeverRestoresCapability();
  TestLegacyAndUnavailableCannotReserve();
}

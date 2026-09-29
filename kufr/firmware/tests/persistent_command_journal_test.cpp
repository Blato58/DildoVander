#include <assert.h>
#include <stddef.h>
#include <stdint.h>

#include <algorithm>
#include <array>

#include "../src/features/device_control/JournalStorage.h"
#include "../src/features/device_control/DeviceControl.h"
#include "../src/features/device_control/PersistentCommandJournal.h"
#include "../src/features/safety/SafetyController.h"

namespace {

constexpr char FirstCommandId[] = "523ef590-ce4d-4901-91b3-79de1bfe7e35";
constexpr char SecondCommandId[] = "7a2fef87-0a80-4115-86f3-3a93ad984b82";

class MemoryJournalStorage final : public JournalStorage {
 public:
  JournalReadResult Read(uint8_t* output, size_t capacity,
                         size_t& length) override {
    if (failRead) {
      return JournalReadResult::Failed;
    }
    if (!present) {
      return JournalReadResult::Missing;
    }
    if (storedLength > capacity) {
      return JournalReadResult::Failed;
    }

    std::copy(data.begin(), data.begin() + storedLength, output);
    length = storedLength;
    return JournalReadResult::Found;
  }

  bool Write(const uint8_t* input, size_t length) override {
    ++writeCount;
    if (failWrite) {
      return false;
    }

    std::copy(input, input + length, data.begin());
    storedLength = length;
    present = true;
    return !reportCommitFailure;
  }

  std::array<uint8_t, PersistentCommandJournal::RecordSize> data{};
  size_t storedLength = 0;
  size_t writeCount = 0;
  bool present = false;
  bool failRead = false;
  bool failWrite = false;
  bool reportCommitFailure = false;
};

class CountingSafetyPort final : public SafetyPort {
 public:
  void DisableActuators() override {}
  void StartVibration(uint8_t) override { ++vibrationCount; }
  void ReleaseLatch(uint8_t) override { ++latchCount; }

  int vibrationCount = 0;
  int latchCount = 0;
};

void TestEmptyAndReloadPersistAcrossRestart() {
  MemoryJournalStorage storage;
  PersistentCommandJournal initial(storage);
  assert(initial.IsHealthy());
  assert(!initial.HasPending());
  assert(initial.ReadyMask() == 0);
  assert(!initial.Begin(FirstCommandId, 1));
  assert(initial.PersistReadyMask(0x01));

  PersistentCommandJournal restarted(storage);
  assert(restarted.IsHealthy());
  assert(!restarted.HasPending());
  assert(restarted.ReadyMask() == 0x01);
  assert(restarted.PersistReadyMask(0x03));
  assert(storage.writeCount == 2);
}

void TestPendingRestartAndAtomicResolve() {
  MemoryJournalStorage storage;
  PersistentCommandJournal initial(storage);
  assert(initial.PersistReadyMask(0x3f));
  assert(initial.Begin(FirstCommandId, 4));
  assert(!initial.Begin(SecondCommandId, 2));
  assert(!initial.PersistReadyMask(0x3f));
  assert(initial.HasPending());
  assert(initial.PendingCompartment() == 4);

  PersistentCommandJournal restarted(storage);
  assert(restarted.HasPending());
  assert(restarted.PendingCompartment() == 4);
  assert(restarted.ReadyMask() == 0x3f);
  assert(!restarted.Resolve(0x3f));
  assert(!restarted.Resolve(0x33));
  assert(restarted.Resolve(0x37));

  PersistentCommandJournal completed(storage);
  assert(completed.IsHealthy());
  assert(!completed.HasPending());
  assert(completed.ReadyMask() == 0x37);
  assert(storage.writeCount == 3);
}

void TestFailedBeginNeverAllowsMotion() {
  MemoryJournalStorage storage;
  PersistentCommandJournal journal(storage);
  assert(journal.PersistReadyMask(0x01));
  storage.failWrite = true;
  assert(!journal.Begin(FirstCommandId, 1));
  assert(!journal.IsHealthy());
  assert(journal.HasPending());
  assert(journal.ReadyMask() == 0);
  assert(!journal.PersistReadyMask(0x02));

  storage.failWrite = false;
  PersistentCommandJournal restarted(storage);
  assert(restarted.IsHealthy());
  assert(!restarted.HasPending());
  assert(restarted.ReadyMask() == 0x01);
}

void TestUncertainCommitRemainsFailClosed() {
  MemoryJournalStorage storage;
  PersistentCommandJournal initial(storage);
  assert(initial.PersistReadyMask(0x03));
  storage.reportCommitFailure = true;
  assert(!initial.Begin(FirstCommandId, 1));
  assert(initial.HasPending());
  assert(!initial.IsHealthy());

  storage.reportCommitFailure = false;
  PersistentCommandJournal restarted(storage);
  assert(restarted.HasPending());
  assert(restarted.PendingCompartment() == 1);
  assert(restarted.ReadyMask() == 0x03);
  storage.failWrite = true;
  assert(!restarted.Resolve(0x02));
  assert(restarted.HasPending());
  assert(restarted.ReadyMask() == 0);

  storage.failWrite = false;
  PersistentCommandJournal afterFailedResolve(storage);
  assert(afterFailedResolve.HasPending());
  assert(afterFailedResolve.ReadyMask() == 0x03);
}

void TestInvalidRecordsAndReadFailureBlockControl() {
  MemoryJournalStorage storage;
  storage.failRead = true;
  PersistentCommandJournal failedRead(storage);
  assert(!failedRead.IsHealthy());
  assert(failedRead.HasPending());
  assert(!failedRead.PersistReadyMask(0x3f));

  storage.failRead = false;
  PersistentCommandJournal empty(storage);
  assert(empty.PersistReadyMask(0x01));
  storage.data[5] = 0xff;
  PersistentCommandJournal invalidMask(storage);
  assert(!invalidMask.IsHealthy());
  assert(invalidMask.HasPending());

  storage.data[5] = 0x01;
  storage.storedLength = PersistentCommandJournal::RecordSize - 1;
  PersistentCommandJournal invalidLength(storage);
  assert(!invalidLength.IsHealthy());
  assert(invalidLength.ReadyMask() == 0);
}

void TestInvalidCommandIdsCannotBeWritten() {
  MemoryJournalStorage storage;
  PersistentCommandJournal journal(storage);
  assert(journal.PersistReadyMask(0x01));
  const size_t baselineWrites = storage.writeCount;
  assert(!journal.Begin("not-a-uuid", 1));
  assert(!journal.Begin(FirstCommandId, 7));
  assert(storage.writeCount == baselineWrites);
  assert(journal.IsHealthy());
}

void TestDeviceControlBlocksMotionWhenJournalWriteFails() {
  MemoryJournalStorage storage;
  PersistentCommandJournal journal(storage);
  assert(journal.PersistReadyMask(0x01));

  CountingSafetyPort port;
  SafetyController safety(port);
  SelectionSequence sequence(safety);
  DeviceControl control("kufr-001", "boot-1", sequence, journal, 1);
  safety.InitializeSafe();
  assert(control.RecoverAfterInspection());
  assert(control.AcquireManualControl("lease-1", 0));

  SelectionCommand command;
  command.commandId = FirstCommandId;
  command.deviceId = "kufr-001";
  command.bootId = "boot-1";
  command.controlLeaseId = "lease-1";
  command.sequence = 1;
  command.expectedRevision = control.Revision();
  command.issuedAtDeviceMs = 0;
  command.maxAgeMs = 2000;
  command.compartment = 1;

  storage.failWrite = true;
  const CommandReceipt receipt = control.AcceptSelection(command, true, 0);
  assert(receipt.error == CommandError::JournalFailed);
  SelectionStep step;
  assert(!control.BeginAcceptedSelection(0, step));
  assert(port.vibrationCount == 0);
  assert(port.latchCount == 0);
  assert(journal.HasPending());
}

void TestPendingRestartRequiresInspectionAndClearsInventory() {
  MemoryJournalStorage storage;
  PersistentCommandJournal initial(storage);
  assert(initial.PersistReadyMask(0x03));
  assert(initial.Begin(FirstCommandId, 2));

  PersistentCommandJournal restarted(storage);
  CountingSafetyPort port;
  SafetyController safety(port);
  SelectionSequence sequence(safety);
  DeviceControl control("kufr-001", "boot-2", sequence, restarted, 1);
  safety.InitializeSafe();
  assert(!control.AcquireManualControl("lease-2", 0));
  assert(control.RecoverAfterInspection());
  assert(control.ReadyMask() == 0x01);
  assert(!restarted.HasPending());
  assert(control.AcquireManualControl("lease-2", 0));
  assert(port.vibrationCount == 0);
  assert(port.latchCount == 0);
}

}  // namespace

int main() {
  TestEmptyAndReloadPersistAcrossRestart();
  TestPendingRestartAndAtomicResolve();
  TestFailedBeginNeverAllowsMotion();
  TestUncertainCommitRemainsFailClosed();
  TestInvalidRecordsAndReadFailureBlockControl();
  TestInvalidCommandIdsCannotBeWritten();
  TestDeviceControlBlocksMotionWhenJournalWriteFails();
  TestPendingRestartRequiresInspectionAndClearsInventory();
  return 0;
}

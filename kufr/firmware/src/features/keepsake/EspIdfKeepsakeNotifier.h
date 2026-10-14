#pragma once

#include <atomic>
#include "KeepsakeOutbox.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

class DeviceIdentity;
class EspIdfStationNetwork;
class JournalStorage;

class EspIdfKeepsakeNotifier final {
 public:
  EspIdfKeepsakeNotifier(DeviceIdentity& identity, EspIdfStationNetwork& station,
                        const std::atomic<bool>& backendOnline,
                        const CommandJournal& ownerJournal, JournalStorage& storage);
  bool Start(bool nvsInitialized);
  bool Prepare(const KeepsakeOutcome& outcome);
  // This acknowledges only the RAM handoff, never durable completion of NVS.
  KeepsakeQueueResult Queue(const KeepsakeOutcome& outcome);
  void Hide() { outbox_.Hide(); }
  LocalKeepsakeStatus Status(uint64_t generation) const;
 private:
  static void TaskEntry(void* context);
  void Run();
  bool Load();
  bool Persist(const KeepsakeOutboxState& state);
  bool PersistCurrent();
  bool Deliver(const KeepsakeOutcome& outcome, const char* accessToken, int& status, std::string& errorCode);
  DeviceIdentity& identity_;
  EspIdfStationNetwork& station_;
  const std::atomic<bool>& backendOnline_;
  KeepsakeOutbox outbox_;
  const CommandJournal& ownerJournal_;
  JournalStorage& journalStorage_;
  std::mutex persistenceMutex_;
  std::atomic<bool> healthy_{false};
  std::string persisted_;
  TaskHandle_t task_ = nullptr;
};

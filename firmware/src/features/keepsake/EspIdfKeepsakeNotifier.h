#pragma once

#include <atomic>
#include <memory>
#include "KeepsakeOutbox.h"
#include "KeepsakeCommittedInventory.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

class DeviceIdentity;
class EspIdfStationNetwork;
class JournalStorage;

class EspIdfKeepsakeNotifier final : public JournalEvidenceSource {
 public:
  EspIdfKeepsakeNotifier(DeviceIdentity& identity, EspIdfStationNetwork& station,
                        const std::atomic<bool>& backendOnline,
                        const CommandJournal& ownerJournal, JournalStorage& storage);
  bool Start(bool nvsInitialized);
  bool ReadCommitted(JournalEvidenceInventory& inventory) const override;
  // Future service integration must hold the common quiescent owner barrier.
  // Construction/Start never calls this binding operation automatically.
  bool EstablishInventory(const std::string& deviceId, bool serviceAuthorized);
  bool Prepare(const KeepsakeOutcome& outcome);
  // This acknowledges only the RAM handoff, never durable completion of NVS.
  KeepsakeQueueResult Queue(const KeepsakeOutcome& outcome);
  void Retire(const KeepsakeOutcome& outcome, const char* reason);
 private:
  static void TaskEntry(void* context);
  void Run();
  bool Load();
  bool Persist(const KeepsakeOutboxState& state);
  bool PersistCurrent();
  bool ProcessRetirements();
  bool ProcessDeliveryAcknowledgements();
  bool Deliver(const KeepsakeOutcome& outcome, const char* accessToken, int& status, std::string& errorCode);
  DeviceIdentity& identity_;
  EspIdfStationNetwork& station_;
  const std::atomic<bool>& backendOnline_;
  KeepsakeOutbox outbox_;
  const CommandJournal& ownerJournal_;
  JournalStorage& journalStorage_;
  mutable std::mutex persistenceMutex_;
  mutable std::atomic<bool> healthy_{false};
  std::unique_ptr<JournalStorage> inventoryStorage_;
  std::unique_ptr<KeepsakeCommittedInventory> committed_;
  TaskHandle_t task_ = nullptr;
};

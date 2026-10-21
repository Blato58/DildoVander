#pragma once

#include <atomic>
#include <cstddef>
#include <new>

#include "DeviceStorageRuntime.h"

// Boot-lifetime composition. Boot/RunOne belong to one storage worker; control
// receives only the facade after startup publication. Normal boot never invokes
// any service establishment, credential mutation or automatic identity binding.
class DeviceStorageStartup final {
 public:
  enum class State : uint8_t { Uninitialized, ServiceRequired, Unavailable, Established };
  DeviceStorageStartup(JournalStorage& journal, JournalStorage& marker,
      JournalStorage& online, JournalStorage& offline, const JournalIdentityView& identity,
      DeviceStorageRuntime::Policy policy = DeviceStorageRuntime::Policy::ReconcileOnly);
  ~DeviceStorageStartup();
  DeviceStorageStartup(const DeviceStorageStartup&) = delete;
  DeviceStorageStartup& operator=(const DeviceStorageStartup&) = delete;

  bool Boot();
  bool RunOne();
  State CurrentState() const { return state_.load(); }
  DeviceStorageRuntime* Runtime() { return runtime_; }
  CommandJournal& ControlJournal() { return runtime_ ? runtime_->ControlJournal() : blocked_; }
  bool ImageStorageHealthy() const;
  bool RequiresService() const;
  bool HasWork() const;

 private:
  class BlockedJournal final : public CommandJournal {
   public:
    void Capture(const PersistentCommandJournal& journal) { journal.ExportSnapshot(snapshot_); }
    bool HasPending() const override { return true; }
    bool HasStorageFault() const override { return true; }
    uint8_t PendingCompartment() const override { return snapshot_.pendingSlot; }
    uint8_t ReadyMask() const override { return snapshot_.readyMask; }
    uint64_t CurrentEpoch() const override { return snapshot_.currentEpoch; }
    bool Begin(const std::string&, uint8_t) override { return false; }
    bool Resolve(uint8_t) override { return false; }
    bool PersistReadyMask(uint8_t) override { return false; }
   private:
    JournalSnapshot snapshot_;
  } blocked_;
  JournalStorage& journal_;
  JournalStorage& marker_;
  JournalStorage& online_;
  JournalStorage& offline_;
  const JournalIdentityView identity_;
  const DeviceStorageRuntime::Policy policy_;
  union Workspace {
    DeviceStorageRuntime runtime;
    Workspace() {}
    ~Workspace() {}
  } workspace_;
  DeviceStorageRuntime* runtime_ = nullptr;
  std::atomic<State> state_{State::Uninitialized};
  std::atomic<bool> workerActive_{false};
  bool bootAttempted_ = false, legacyImageHealthy_ = false;
};

#pragma once

#include "JournalStorageOwner.h"

// Control-thread facade. It contains only published observations and one
// immutable outstanding request; the storage worker owns the mutable journal.
class AsyncCommandJournal final : public CommandJournal {
 public:
  AsyncCommandJournal(JournalStorageOwner& owner, JournalTransactionArbiter& arbiter);
  StorageState LastStorageState() const override { return lastState_; }
  bool HasPendingStorage() const override { return token_.id != 0; }
  void PollStorage() override;
  void CancelStorageAdmissions() override;
  void BeginStorageRecovery() override;
  bool ResumeStorageAfterRecovery() override;
  bool HasPending() const override { return View().HasPending(); }
  bool CanBeginOperation() const override { return !token_.id && !arbiter_.AdmissionsStopped() && View().CanBeginOperation(); }
  uint8_t PendingCompartment() const override { return View().PendingCompartment(); }
  uint8_t ReadyMask() const override { return View().ReadyMask(); }
  uint64_t CurrentEpoch() const override { return View().CurrentEpoch(); }
  Lookup Find(const std::string& id, uint8_t slot, uint64_t fingerprint) const override {
    return View().Find(id, slot, fingerprint);
  }
  Lookup FindScoped(uint64_t epoch, const std::string& id, uint8_t slot, uint64_t fingerprint) const override {
    return View().FindScoped(epoch, id, slot, fingerprint);
  }
  Lookup FindHistorical(uint64_t epoch, const std::string& id, uint8_t slot, uint64_t fingerprint) const override {
    return View().FindHistorical(epoch, id, slot, fingerprint);
  }
  bool WasTimedPresentationCompleted(const std::string& id, uint8_t slot) const override {
    return View().WasTimedPresentationCompleted(id, slot);
  }
  bool WasLegacyTimedPresentationCompleted(const std::string& id, uint8_t slot) const override {
    return View().WasLegacyTimedPresentationCompleted(id, slot);
  }
  bool WasTimedPresentationCompletedScoped(uint64_t epoch, const std::string& id, uint8_t slot,
                                            uint64_t fingerprint) const override {
    return View().WasTimedPresentationCompletedScoped(epoch, id, slot, fingerprint);
  }
  bool WasTimedPresentationCompletedHistorical(uint64_t epoch, const std::string& id, uint8_t slot,
                                                uint64_t fingerprint) const override {
    return View().WasTimedPresentationCompletedHistorical(epoch, id, slot, fingerprint);
  }
  bool HasPresentationReservation() const override {
    return !arbiter_.AdmissionsStopped() && snapshotCancellation_ == arbiter_.Cancellation() &&
        View().HasPresentationReservation();
  }
  bool HasPresentationReservation(uint64_t epoch, const std::string& id, uint8_t slot,
                                   uint64_t fingerprint) const override {
    return !arbiter_.AdmissionsStopped() && snapshotCancellation_ == arbiter_.Cancellation() &&
        View().HasPresentationReservation(epoch, id, slot, fingerprint);
  }
  bool Begin(const std::string& id, uint8_t slot) override { return BeginWithIdentity(id, slot, 0); }
  bool BeginWithIdentity(const std::string& id, uint8_t slot, uint64_t fingerprint) override;
  bool BeginScoped(uint64_t epoch, const std::string& id, uint8_t slot, uint64_t fingerprint) override;
  bool ReservePresentation(uint64_t epoch, const std::string& id, uint8_t slot, uint64_t fingerprint) override;
  bool BeginReservedPresentation(uint64_t epoch, const std::string& id, uint8_t slot, uint64_t fingerprint) override;
  void CancelPresentationReservation() override;
  bool Resolve(uint8_t readyMask) override;
  bool ResolveRestored(uint8_t slot) override;
  bool ConfirmTimedPresentation(const std::string& id, uint8_t slot, uint64_t fingerprint) override;
  bool ConfirmTimedPresentationScoped(uint64_t epoch, const std::string& id, uint8_t slot, uint64_t fingerprint) override;
  bool PersistReadyMask(uint8_t readyMask) override;
  bool HasStorageIntent() const { return token_.id != 0; }

 private:
  using Request = JournalStorageOwner::Request;
  using Purpose = JournalStorageOwner::Purpose;
  bool Apply(const Request& request, Purpose purpose);
  bool Identified(JournalStorageOwner::Operation operation, Purpose purpose, uint64_t epoch,
                  const std::string& id, uint8_t slot, uint64_t fingerprint);
  static bool Same(const Request& left, const Request& right);
  JournalSnapshotView View() const { return JournalSnapshotView(snapshot_); }
  void Adopt(const JournalStorageOwner::Completion& completion);

  JournalStorageOwner& owner_;
  JournalTransactionArbiter& arbiter_;
  JournalSnapshot snapshot_;
  Request request_;
  JournalStorageOwner::Token token_;
  StorageState lastState_ = StorageState::Rejected;
  bool abandoned_ = false;
  uint32_t recoveryCancellation_ = 0;
  uint32_t snapshotCancellation_ = 0;
};

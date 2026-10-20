#include "DeviceStorageStartup.h"

DeviceStorageStartup::DeviceStorageStartup(JournalStorage& journal, JournalStorage& marker,
    JournalStorage& online, JournalStorage& offline, const JournalIdentityView& identity,
    DeviceStorageRuntime::Policy policy)
    : journal_(journal), marker_(marker), online_(online), offline_(offline), identity_(identity), policy_(policy) {}

DeviceStorageStartup::~DeviceStorageStartup() {
  // Production owns this object for the entire boot. Host callers must stop the
  // worker and all consumers before ending the fixture's lifetime.
  if (runtime_) runtime_->~DeviceStorageRuntime();
}

bool DeviceStorageStartup::Boot() {
  if (bootAttempted_) return false;
  bootAttempted_ = true;
  bool established = false;
  {
    // These read-only probes are destroyed before the mutable runtime exists.
    // In particular, persisted OriginMissing is not actual storage absence.
    PersistentCommandJournal journal(journal_);
    JournalIdentityOwner marker(marker_, journal);
    blocked_.Capture(journal);
    if (!identity_.healthy || !journal.IsHealthy() || journal.IsStorageMissing() || !marker.IsHealthy()) {
      state_.store(State::Unavailable);
      return false;
    }
    established = marker.HasEstablishedBinding(identity_);
    legacyImageHealthy_ = journal.CurrentEpoch() == 0 &&
        journal.Origin() != PersistentCommandJournal::StateOrigin::Scoped &&
        (marker.CurrentPhase() == JournalIdentityOwner::Phase::Missing ||
         (marker.CurrentPhase() == JournalIdentityOwner::Phase::PreparingLegacy &&
          marker.Validate(identity_) == JournalIdentityOwner::Validation::Preparing));
  }
  if (!established) {
    state_.store(State::ServiceRequired);
    return false;
  }
  runtime_ = new (&workspace_.runtime) DeviceStorageRuntime(journal_, marker_, online_, offline_, identity_, policy_);
  runtime_->Boot();
  // Initialization handoff occurs before any control consumer is constructed.
  // Adopt the post-reconciliation epoch/mask rather than the constructor's older
  // publication. Subsequent facade access belongs exclusively to control.
  runtime_->ControlJournal().PollStorage();
  const bool healthy = ImageStorageHealthy();
  state_.store(healthy ? State::Established : State::Unavailable);
  return healthy;
}

bool DeviceStorageStartup::RunOne() {
  if (!runtime_) return false;
  workerActive_.store(true);
  const bool worked = runtime_->RunOne();
  workerActive_.store(false);
  return worked;
}

bool DeviceStorageStartup::ImageStorageHealthy() const {
  if (!runtime_) return legacyImageHealthy_;
  const auto state = runtime_->CurrentState();
  return state == DeviceStorageRuntime::State::Ready || state == DeviceStorageRuntime::State::InspectionRequired ||
      state == DeviceStorageRuntime::State::CapacityBlocked;
}

bool DeviceStorageStartup::RequiresService() const {
  return !runtime_ || !runtime_->AllowsRecovery();
}

bool DeviceStorageStartup::HasWork() const {
  return workerActive_.load() || (runtime_ &&
      (runtime_->HasPendingJournalWork() || runtime_->EvidenceRequests().Busy()));
}

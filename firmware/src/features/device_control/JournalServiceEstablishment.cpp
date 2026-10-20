#include "JournalServiceEstablishment.h"

#include <algorithm>

JournalServiceEstablishment::JournalServiceEstablishment(
    DeviceIdentity& identity, const ServiceGate& gate,
    JournalIdentityOwner& witness, JournalServiceInventory& online,
    JournalServiceInventory& offline, JournalReconciliation& reconciliation)
    : identity_(identity), gate_(gate), witness_(witness),
      online_(online), offline_(offline), reconciliation_(reconciliation) {}

JournalIdentityView JournalServiceEstablishment::View() const {
  return {identity_.IsHealthy(), identity_.IsProvisioned(), identity_.DeviceId()};
}

bool JournalServiceEstablishment::ReadInventories() {
  return online_.ReadCommitted(onlineInventory_) && offline_.ReadCommitted(offlineInventory_);
}

bool JournalServiceEstablishment::FactoryInventory(
    const JournalEvidenceInventory& inventory, const std::string& target) const {
  if (inventory.origin == JournalInventoryOrigin::Missing) return true;
  // Only a previous factory step may have established an empty bound inventory.
  // A found unbound inventory is never relabelled as a fresh factory store.
  return inventory.origin == JournalInventoryOrigin::Valid && inventory.complete &&
      inventory.deviceId == target && inventory.generation != 0 &&
      !inventory.ambiguous && inventory.count == 0;
}

JournalServiceEstablishment::Result JournalServiceEstablishment::CheckEvidence() {
  const auto result = reconciliation_.Reconcile(View(), false);
  return result == JournalReconciliation::Result::Reconciled ||
      result == JournalReconciliation::Result::NotNeeded ? Result::Established : Result::EvidenceBlocked;
}

JournalServiceEstablishment::Result JournalServiceEstablishment::Run(
    Mode mode, const std::string& target,
    const std::array<uint8_t, DeviceIdentity::CredentialSize>* credential) {
  if (!gate_.ServiceModeActive() || !gate_.ActuatorSupplyDisconnected()) return Result::ServiceDenied;
  if (target.empty() || (mode != Mode::Factory && mode != Mode::Legacy && mode != Mode::Resume) ||
      (credential && std::all_of(credential->begin(), credential->end(),
          [](uint8_t byte) { return byte == 0; }))) return Result::InvalidRequest;
  const auto identity = View();
  if (!identity.healthy) return Result::IdentityUnavailable;
  // Journal establishment cannot rotate credentials or rebind a device. An
  // interrupted factory with a committed identity resumes without credentials.
  if (identity.provisioned && (identity.deviceId != target || credential))
    return Result::IdentityReplacementDenied;
  if (!identity.provisioned && (!credential || mode == Mode::Legacy)) return Result::InvalidRequest;
  if (!ReadInventories()) return Result::InventoryRejected;

  if (mode == Mode::Factory) {
    if (onlineInventory_.origin != JournalInventoryOrigin::Missing ||
        offlineInventory_.origin != JournalInventoryOrigin::Missing)
      return Result::InventoryRejected;
    if (!witness_.BeginFactory(target, identity, true)) return Result::MarkerRejected;
  } else if (mode == Mode::Legacy) {
    // Missing legacy inventories may mean lost evidence, so explicit adoption
    // still requires both actual stores to be present. Unknown associations stay.
    if (onlineInventory_.origin != JournalInventoryOrigin::Valid ||
        offlineInventory_.origin != JournalInventoryOrigin::Valid)
      return Result::InventoryRejected;
    if (!witness_.BeginLegacy(identity, true)) return Result::MarkerRejected;
  } else {
    if (witness_.TargetDeviceId() != target) return Result::MarkerRejected;
    if (witness_.CurrentPhase() == JournalIdentityOwner::Phase::Established) {
      // Established loss is never repaired by recreating an empty inventory.
      if (witness_.Validate(identity) != JournalIdentityOwner::Validation::Ready)
        return Result::MarkerRejected;
      return CheckEvidence();
    }
    if (witness_.CurrentPhase() != JournalIdentityOwner::Phase::PreparingFactory &&
        witness_.CurrentPhase() != JournalIdentityOwner::Phase::PreparingLegacy)
      return Result::MarkerRejected;
  }

  if (witness_.CurrentPhase() == JournalIdentityOwner::Phase::PreparingFactory) {
    if (!FactoryInventory(onlineInventory_, target) || !FactoryInventory(offlineInventory_, target))
      return Result::InventoryRejected;
  } else if (onlineInventory_.origin != JournalInventoryOrigin::Valid ||
             offlineInventory_.origin != JournalInventoryOrigin::Valid) {
    return Result::InventoryRejected;
  }
  if (!witness_.ResumePreparation(identity, true)) return Result::JournalRejected;
  if (!online_.EstablishBinding(target, true) || !offline_.EstablishBinding(target, true))
    return Result::InventoryWriteFailed;
  if (!ReadInventories()) return Result::InventoryRejected;
  // The witness and both inventory bindings must be durable before credentials.
  if (!identity.provisioned && !identity_.Provision(target, *credential, gate_))
    return Result::IdentityWriteFailed;
  if (!witness_.Finalize(View(), onlineInventory_, offlineInventory_, true))
    return Result::FinalizationFailed;
  return CheckEvidence();
}

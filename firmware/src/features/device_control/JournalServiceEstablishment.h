#pragma once

#include "DeviceIdentity.h"
#include "JournalIdentityOwner.h"
#include "JournalReconciliation.h"

// Synchronous, exclusive USB service-image owner. Never call from production
// control/Stop or alongside live reference producers. No operation grants motion
// authority: production boot must independently reconcile committed sources.
class JournalServiceEstablishment final {
 public:
  enum class Mode { Factory, Legacy, Resume };
  enum class Result {
    Established, ServiceDenied, InvalidRequest, IdentityUnavailable,
    IdentityReplacementDenied, InventoryRejected, MarkerRejected,
    JournalRejected, InventoryWriteFailed, IdentityWriteFailed,
    FinalizationFailed, EvidenceBlocked
  };
  JournalServiceEstablishment(DeviceIdentity& identity, const ServiceGate& gate,
      JournalIdentityOwner& witness,
      JournalServiceInventory& online, JournalServiceInventory& offline,
      JournalReconciliation& reconciliation);
  Result Run(Mode mode, const std::string& target,
      const std::array<uint8_t, DeviceIdentity::CredentialSize>* credential = nullptr);

 private:
  JournalIdentityView View() const;
  bool ReadInventories();
  bool FactoryInventory(const JournalEvidenceInventory& inventory,
                        const std::string& target) const;
  Result CheckEvidence();

  DeviceIdentity& identity_;
  const ServiceGate& gate_;
  JournalIdentityOwner& witness_;
  JournalServiceInventory& online_;
  JournalServiceInventory& offline_;
  JournalReconciliation& reconciliation_;
  // Owned workspace, not temporary service-task stack allocations.
  JournalEvidenceInventory onlineInventory_, offlineInventory_;
};

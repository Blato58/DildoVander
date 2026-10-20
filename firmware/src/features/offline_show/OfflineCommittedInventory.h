#pragma once

#include <array>
#include <string>

#include "OfflineShow.h"
#include "../device_control/JournalEvidence.h"
#include "../device_control/JournalStorage.h"

namespace kufr { namespace offline {

// Storage/source foundation only. Service binding is never inferred from boot,
// Missing storage or an existing legacy outbox. Production remains unbound.
// A future bound composition must serialize these calls with every producer and
// persisted-journal reader under the common reconciliation barrier. Binding and
// reopen are synchronous service/startup work, never control-loop operations.
class OfflineCommittedInventory final : public StoragePort, public JournalServiceInventory {
 public:
  static constexpr size_t MaximumDeviceIdLength = 64;
  static constexpr size_t EnvelopeHeaderSize = 104;
  static constexpr size_t OutcomeRecordSize = 163;
  static constexpr size_t EnvelopeSize = EnvelopeHeaderSize + 8 * OutcomeRecordSize + 4;
  static constexpr size_t MaximumRecordSize = EnvelopeSize;

  explicit OfflineCommittedInventory(JournalStorage& storage);
  OfflineCommittedInventory(const OfflineCommittedInventory&) = delete;
  OfflineCommittedInventory& operator=(const OfflineCommittedInventory&) = delete;
  bool Reopen();
  bool Read(Outbox& outbox) override;
  bool Write(const Outbox& outbox) override;
  bool ReadCommitted(JournalEvidenceInventory& inventory) const override;
  bool EstablishBinding(const std::string& deviceId, bool serviceAuthorized) override;

  bool IsHealthy() const { return healthy_; }
  bool IsBound() const { return healthy_ && bound_; }
  uint64_t Generation() const { return healthy_ && bound_ ? generation_ : 0; }
  JournalInventoryOrigin Origin() const {
    return healthy_ ? origin_ : JournalInventoryOrigin::Unavailable;
  }
  const std::string& BoundDeviceId() const { return deviceId_; }

 private:
  bool PersistBound(const Outbox& outbox, const std::string& deviceId, uint64_t generation);
  bool DecodeEnvelope(size_t length);
  void EncodeEnvelope(const Outbox& outbox, const std::string& deviceId, uint64_t generation);
  static bool ValidDeviceId(const std::string& deviceId);
  static bool ValidRecords(const Outbox& outbox);
  static bool AmbiguousRecord(const Outcome& record);
  static bool EmptyRecord(const Outcome& record);

  JournalStorage& storage_;
  Outbox committed_;
  std::array<uint8_t, MaximumRecordSize> bytes_{};
  std::string deviceId_;
  uint64_t generation_ = 0;
  JournalInventoryOrigin origin_ = JournalInventoryOrigin::Unavailable;
  bool healthy_ = false;
  bool bound_ = false;
};

} }

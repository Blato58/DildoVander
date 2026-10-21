#pragma once

#include "nvs.h"
#include "OfflineCommittedInventory.h"

namespace kufr { namespace offline {
// Raw committed bytes for the single production storage worker. Constructing
// this adapter never creates a valid empty inventory or establishes a binding.
class EspIdfOfflineBlobStorage final : public JournalStorage {
 public:
  explicit EspIdfOfflineBlobStorage(bool initialized);
  ~EspIdfOfflineBlobStorage() override;
  bool Reopen();
  JournalReadResult Read(uint8_t* bytes, size_t capacity, size_t& length) override;
  bool Write(const uint8_t* bytes, size_t length) override;
 private:
  nvs_handle_t handle_ = 0;
  bool initialized_ = false, opened_ = false, usable_ = false;
};

class EspIdfOutboxStorage final : public StoragePort, public JournalServiceInventory {
 public:
  explicit EspIdfOutboxStorage(bool initialized);
  bool Read(Outbox& outbox) override;
  bool Write(const Outbox& outbox) override;
  bool ReadCommitted(JournalEvidenceInventory& inventory) const override;
  // Explicit synchronous service/startup operations. No production caller binds.
  bool EstablishBinding(const std::string& deviceId, bool serviceAuthorized) override;
  bool Reopen();
 private:
  EspIdfOfflineBlobStorage bytes_;
  OfflineCommittedInventory inventory_;
};

} }

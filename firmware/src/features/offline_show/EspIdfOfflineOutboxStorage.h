#pragma once

#include "nvs.h"
#include "OfflineCommittedInventory.h"

namespace kufr { namespace offline {
class EspIdfOutboxStorage final : public StoragePort, public JournalEvidenceSource {
 public:
  explicit EspIdfOutboxStorage(bool initialized);
  bool Read(Outbox& outbox) override;
  bool Write(const Outbox& outbox) override;
  bool ReadCommitted(JournalEvidenceInventory& inventory) const override;
  // Explicit synchronous service/startup operations. No production caller binds.
  bool EstablishBinding(const std::string& deviceId, bool serviceAuthorized);
  bool Reopen();
 private:
  class BlobStorage final : public JournalStorage {
   public:
    explicit BlobStorage(bool initialized);
    ~BlobStorage() override;
    bool Reopen();
    JournalReadResult Read(uint8_t* bytes, size_t capacity, size_t& length) override;
    bool Write(const uint8_t* bytes, size_t length) override;
   private:
    nvs_handle_t handle_ = 0;
    bool initialized_ = false, opened_ = false, usable_ = false;
  };
  BlobStorage bytes_;
  OfflineCommittedInventory inventory_;
};

} }

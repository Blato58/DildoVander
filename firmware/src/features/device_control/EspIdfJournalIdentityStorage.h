#pragma once

#include "JournalStorage.h"
#include "nvs.h"

// Kept beside the provisioned device record, independently of journal history.
// A failed write requires a new handle before authoritative recovery/readback.
class EspIdfJournalIdentityStorage final : public JournalStorage {
 public:
  explicit EspIdfJournalIdentityStorage(bool nvsInitialized);
  ~EspIdfJournalIdentityStorage() override;

  JournalReadResult Read(uint8_t* data, size_t capacity, size_t& length) override;
  bool Write(const uint8_t* data, size_t length) override;

 private:
  nvs_handle_t handle_ = 0;
  bool opened_ = false;
  bool usable_ = false;
};

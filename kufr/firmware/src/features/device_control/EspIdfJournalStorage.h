#pragma once

#include "nvs.h"

#include "JournalStorage.h"

class EspIdfJournalStorage final : public JournalStorage {
 public:
  explicit EspIdfJournalStorage(bool nvsInitialized);
  ~EspIdfJournalStorage() override;

  JournalReadResult Read(uint8_t* data, size_t capacity,
                         size_t& length) override;
  bool Write(const uint8_t* data, size_t length) override;

 private:
  nvs_handle_t handle_ = 0;
  bool opened_ = false;
};

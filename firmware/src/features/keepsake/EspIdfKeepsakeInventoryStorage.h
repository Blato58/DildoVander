#pragma once

#include "../device_control/JournalStorage.h"

// Dedicated producer namespace; each read observes committed NVS bytes and does
// not create a missing namespace. The owning inventory handles failure poison.
class EspIdfKeepsakeInventoryStorage final : public JournalStorage {
 public:
  explicit EspIdfKeepsakeInventoryStorage(bool nvsInitialized) : initialized_(nvsInitialized) {}
  JournalReadResult Read(uint8_t* bytes, size_t capacity, size_t& length) override;
  bool Write(const uint8_t* bytes, size_t length) override;
 private:
  bool initialized_ = false;
};

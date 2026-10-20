#pragma once

#include <string>
#include "device_control/JournalStorage.h"

class FileJournal final : public JournalStorage {
 public:
  explicit FileJournal(const std::string& path);
  ~FileJournal() override;
  JournalReadResult Read(uint8_t* data, size_t capacity, size_t& length) override;
  bool Write(const uint8_t* data, size_t length) override;
  bool failWrites = false;
 private:
  std::string path_;
  intptr_t lock_ = -1;
};

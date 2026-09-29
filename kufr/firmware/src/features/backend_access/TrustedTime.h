#pragma once

#include <stdint.h>

enum class LastTrustedTimeRead {
  Missing,
  Found,
  Failed,
};

class LastTrustedTimeStore {
 public:
  virtual ~LastTrustedTimeStore() = default;
  virtual LastTrustedTimeRead Read(uint64_t& epochSeconds) = 0;
  virtual bool Write(uint64_t epochSeconds) = 0;
};

class TrustedTime final {
 public:
  TrustedTime(LastTrustedTimeStore& store, uint64_t buildEpochSeconds);
  bool CheckAndRecord(uint64_t nowEpochSeconds);
  bool IsHealthy() const;

 private:
  LastTrustedTimeStore& store_;
  uint64_t buildEpochSeconds_;
  uint64_t lastTrustedEpochSeconds_ = 0;
  bool healthy_ = false;
};

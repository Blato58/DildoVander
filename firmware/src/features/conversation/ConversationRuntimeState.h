#pragma once

#include <stdint.h>

#include <mutex>
#include <string>

struct ConversationRuntimeSnapshot {
  uint64_t generation = 0;
  uint64_t deviceRevision = 0;
  uint64_t version = 0;
  uint8_t readyMask = 0;
  std::string catalogVersion;
};

// The control owner publishes one coherent view after every journal/control
// transition. Network code may only use a snapshot while its version matches.
class ConversationRuntimeState final {
 public:
  ConversationRuntimeState(bool available, uint8_t readyMask,
                           const std::string& catalogVersion);

  bool Read(ConversationRuntimeSnapshot& snapshot) const;
  bool Matches(const ConversationRuntimeSnapshot& snapshot) const;
  bool Publish(uint64_t generation, uint64_t deviceRevision,
               uint8_t readyMask, const std::string& catalogVersion);
  void Invalidate();

 private:
  mutable std::mutex mutex_;
  ConversationRuntimeSnapshot snapshot_;
  bool available_ = false;
};

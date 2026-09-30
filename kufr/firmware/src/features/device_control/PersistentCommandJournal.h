#pragma once

#include <stdint.h>

#include <array>
#include <string>

#include "CommandJournal.h"
#include "JournalStorage.h"

class PersistentCommandJournal final : public CommandJournal {
 public:
  static constexpr size_t RecordSize = 44;

  explicit PersistentCommandJournal(JournalStorage& storage);

  bool IsHealthy() const;
  bool HasPending() const override;
  uint8_t PendingCompartment() const override;
  uint8_t ReadyMask() const override;
  bool Begin(const std::string& commandId, uint8_t compartment) override;
  bool Resolve(uint8_t readyMask) override;
  bool ResolveRestored(uint8_t compartment) override;
  bool PersistReadyMask(uint8_t readyMask) override;

 private:
  struct State {
    uint8_t readyMask = 0;
    uint8_t pendingCompartment = 0;
    std::string commandId;
  };

  bool Persist(const State& next);
  static bool Decode(const uint8_t* data, size_t length, State& state);
  static void Encode(const State& state,
                     std::array<uint8_t, RecordSize>& data);
  static bool IsCanonicalUuid(const std::string& value);

  JournalStorage& storage_;
  State state_;
  bool healthy_ = false;
};

#include "PersistentCommandJournal.h"

#include <algorithm>

namespace {

constexpr uint8_t Magic[] = {'K', 'J', 'R', '1'};
constexpr uint8_t RecordVersion = 1;
constexpr size_t CommandIdOffset = 8;
constexpr size_t CommandIdLength = 36;
constexpr uint8_t AllCompartmentsMask = 0x3f;

bool IsHexDigit(char value) {
  return (value >= '0' && value <= '9') ||
         (value >= 'a' && value <= 'f') ||
         (value >= 'A' && value <= 'F');
}

}  // namespace

PersistentCommandJournal::PersistentCommandJournal(JournalStorage& storage)
    : storage_(storage) {
  std::array<uint8_t, RecordSize> data{};
  size_t length = 0;
  const JournalReadResult result = storage_.Read(data.data(), data.size(),
                                                 length);
  if (result == JournalReadResult::Missing) {
    healthy_ = true;
  } else if (result == JournalReadResult::Found &&
             Decode(data.data(), length, state_)) {
    healthy_ = true;
  }
}

bool PersistentCommandJournal::IsHealthy() const { return healthy_; }

bool PersistentCommandJournal::HasPending() const {
  return !healthy_ || state_.pendingCompartment != 0;
}

uint8_t PersistentCommandJournal::PendingCompartment() const {
  return healthy_ ? state_.pendingCompartment : 0;
}

uint8_t PersistentCommandJournal::ReadyMask() const {
  return healthy_ ? state_.readyMask : 0;
}

bool PersistentCommandJournal::Begin(const std::string& commandId,
                                     uint8_t compartment) {
  if (HasPending() || !IsCanonicalUuid(commandId) || compartment < 1 ||
      compartment > 6 ||
      (state_.readyMask & (1u << (compartment - 1))) == 0) {
    return false;
  }

  State next = state_;
  next.pendingCompartment = compartment;
  next.commandId = commandId;
  return Persist(next);
}

bool PersistentCommandJournal::Resolve(uint8_t readyMask) {
  if (!healthy_ || state_.pendingCompartment == 0 ||
      readyMask > AllCompartmentsMask ||
      readyMask != static_cast<uint8_t>(
                       state_.readyMask &
                       ~(1u << (state_.pendingCompartment - 1)))) {
    return false;
  }

  State next;
  next.readyMask = readyMask;
  return Persist(next);
}

bool PersistentCommandJournal::PersistReadyMask(uint8_t readyMask) {
  if (HasPending() || readyMask > AllCompartmentsMask) {
    return false;
  }

  State next = state_;
  next.readyMask = readyMask;
  return Persist(next);
}

bool PersistentCommandJournal::Persist(const State& next) {
  std::array<uint8_t, RecordSize> data{};
  Encode(next, data);
  if (!storage_.Write(data.data(), data.size())) {
    healthy_ = false;
    return false;
  }

  state_ = next;
  return true;
}

bool PersistentCommandJournal::Decode(const uint8_t* data, size_t length,
                                      State& state) {
  if (length != RecordSize ||
      !std::equal(Magic, Magic + sizeof(Magic), data) ||
      data[4] != RecordVersion || data[5] > AllCompartmentsMask ||
      data[6] > 6 || data[7] != 0) {
    return false;
  }

  State decoded;
  decoded.readyMask = data[5];
  decoded.pendingCompartment = data[6];
  if (decoded.pendingCompartment == 0) {
    for (size_t index = CommandIdOffset; index < RecordSize; ++index) {
      if (data[index] != 0) {
        return false;
      }
    }
  } else {
    decoded.commandId.assign(
        reinterpret_cast<const char*>(data + CommandIdOffset),
        CommandIdLength);
    if (!IsCanonicalUuid(decoded.commandId)) {
      return false;
    }
  }

  state = decoded;
  return true;
}

void PersistentCommandJournal::Encode(
    const State& state, std::array<uint8_t, RecordSize>& data) {
  std::copy(Magic, Magic + sizeof(Magic), data.begin());
  data[4] = RecordVersion;
  data[5] = state.readyMask;
  data[6] = state.pendingCompartment;
  if (state.pendingCompartment != 0) {
    std::copy(state.commandId.begin(), state.commandId.end(),
              data.begin() + CommandIdOffset);
  }
}

bool PersistentCommandJournal::IsCanonicalUuid(const std::string& value) {
  if (value.size() != CommandIdLength) {
    return false;
  }

  for (size_t index = 0; index < value.size(); ++index) {
    const bool hyphenPosition = index == 8 || index == 13 || index == 18 ||
                                index == 23;
    if (hyphenPosition ? value[index] != '-' : !IsHexDigit(value[index])) {
      return false;
    }
  }

  return true;
}

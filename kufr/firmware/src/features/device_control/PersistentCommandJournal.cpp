#include "PersistentCommandJournal.h"

#include <algorithm>

namespace {

constexpr uint8_t Magic[] = {'K', 'J', 'R', '1'};
constexpr uint8_t RecordVersion = 2;
constexpr size_t CommandIdOffset = 8;
constexpr size_t CommandIdLength = 36;
constexpr uint8_t AllCompartmentsMask = 0x3f;

bool IsHexDigit(char value) {
  return (value >= '0' && value <= '9') ||
         (value >= 'a' && value <= 'f') ||
         (value >= 'A' && value <= 'F');
}

bool SameUuid(const std::string& left, const std::string& right) {
  if (left.size() != right.size()) return false;
  for (size_t index = 0; index < left.size(); ++index) {
    const char a = left[index] >= 'A' && left[index] <= 'F' ? left[index] + ('a' - 'A') : left[index];
    const char b = right[index] >= 'A' && right[index] <= 'F' ? right[index] + ('a' - 'A') : right[index];
    if (a != b) return false;
  }
  return true;
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
  return BeginWithIdentity(commandId, compartment, 0);
}

PersistentCommandJournal::Lookup PersistentCommandJournal::Find(
    const std::string& commandId, uint8_t compartment, uint64_t fingerprint) const {
  for (size_t i = 0; i < state_.historyCount; ++i) {
    const auto& entry = state_.history[i];
    if (SameUuid(entry.id, commandId)) return entry.compartment == compartment && entry.fingerprint == fingerprint
        ? Lookup::Same : Lookup::Conflict;
  }
  return Lookup::Missing;
}

bool PersistentCommandJournal::BeginWithIdentity(const std::string& commandId,
                                                 uint8_t compartment,
                                                 uint64_t fingerprint) {
  if (HasPending() || state_.historyCount == HistoryCapacity ||
      Find(commandId, compartment, fingerprint) != Lookup::Missing || !IsCanonicalUuid(commandId) || compartment < 1 ||
      compartment > 6 ||
      (state_.readyMask & (1u << (compartment - 1))) == 0) {
    return false;
  }

  State next = state_;
  next.pendingCompartment = compartment;
  next.commandId = commandId;
  auto& entry = next.history[next.historyCount++];
  entry.id = commandId; entry.compartment = compartment; entry.fingerprint = fingerprint;
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

  State next = state_;
  next.commandId.clear(); next.pendingCompartment = 0;
  next.readyMask = readyMask;
  return Persist(next);
}

bool PersistentCommandJournal::ResolveRestored(uint8_t compartment) {
  if (!healthy_ || state_.pendingCompartment == 0 ||
      state_.pendingCompartment != compartment ||
      (state_.readyMask & (1u << (compartment - 1))) == 0) {
    return false;
  }

  State next = state_;
  next.commandId.clear(); next.pendingCompartment = 0;
  next.readyMask = state_.readyMask;
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
  const bool legacy = length == 44 && data[4] == 1;
  if ((!legacy && length != RecordSize) ||
      !std::equal(Magic, Magic + sizeof(Magic), data) ||
      (!legacy && data[4] != RecordVersion) || data[5] > AllCompartmentsMask ||
      data[6] > 6 || (legacy ? data[7] != 0 : data[7] > HistoryCapacity)) {
    return false;
  }

  State decoded;
  decoded.readyMask = data[5];
  decoded.pendingCompartment = data[6];
  if (decoded.pendingCompartment == 0) {
    for (size_t index = CommandIdOffset; index < 44; ++index) {
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

  if (legacy) {
    // Old completed records never stored an ID; it cannot be reconstructed.
    if (decoded.pendingCompartment) {
      decoded.historyCount = 1;
      decoded.history[0].id = decoded.commandId;
      decoded.history[0].compartment = decoded.pendingCompartment;
    }
  } else {
    decoded.historyCount = data[7];
    for (size_t i = 0; i < HistoryCapacity; ++i) {
      const size_t offset = 44 + i * 45;
      if (i >= decoded.historyCount) {
        for (size_t j = offset; j < offset + 45; ++j) if (data[j]) return false;
        continue;
      }
      auto& entry = decoded.history[i];
      entry.id.assign(reinterpret_cast<const char*>(data + offset), CommandIdLength);
      entry.compartment = data[offset + 36];
      if (!IsCanonicalUuid(entry.id) || entry.compartment < 1 || entry.compartment > 6) return false;
      for (unsigned byte = 0; byte < 8; ++byte)
        entry.fingerprint |= static_cast<uint64_t>(data[offset + 37 + byte]) << (8 * byte);
      for (size_t j = 0; j < i; ++j) if (SameUuid(decoded.history[j].id, entry.id)) return false;
    }
    if (decoded.pendingCompartment) {
      bool found = false;
      for (size_t i = 0; i < decoded.historyCount; ++i)
        found |= SameUuid(decoded.history[i].id, decoded.commandId) &&
                 decoded.history[i].compartment == decoded.pendingCompartment;
      if (!found) return false;
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
  data[7] = state.historyCount;
  for (size_t i = 0; i < state.historyCount; ++i) {
    const size_t offset = 44 + i * 45;
    const auto& entry = state.history[i];
    std::copy(entry.id.begin(), entry.id.end(), data.begin() + offset);
    data[offset + 36] = entry.compartment;
    for (unsigned byte = 0; byte < 8; ++byte)
      data[offset + 37 + byte] = static_cast<uint8_t>(entry.fingerprint >> (8 * byte));
  }
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

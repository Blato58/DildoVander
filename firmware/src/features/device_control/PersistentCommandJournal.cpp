#include "PersistentCommandJournal.h"

#include <algorithm>
#include <limits>

namespace {

constexpr uint8_t Magic[] = {'K', 'J', 'R', '1'};
constexpr uint8_t RecordVersion = 4;
constexpr size_t CommandIdOffset = 8;
constexpr size_t CommandIdLength = 36;
constexpr size_t LegacyRecordSize = 44 + PersistentCommandJournal::HistoryCapacity * 45;
constexpr size_t EntryOffset = 118;
constexpr size_t EntrySize = 54;
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

uint64_t Read64(const uint8_t* data) {
  uint64_t value = 0;
  for (unsigned byte = 0; byte < 8; ++byte) value |= static_cast<uint64_t>(data[byte]) << (8 * byte);
  return value;
}

void Write64(uint8_t* data, uint64_t value) {
  for (unsigned byte = 0; byte < 8; ++byte) data[byte] = static_cast<uint8_t>(value >> (8 * byte));
}

uint32_t Crc32(const uint8_t* data, size_t length) {
  uint32_t crc = 0xffffffff;
  for (size_t index = 0; index < length; ++index) {
    crc ^= data[index];
    for (unsigned bit = 0; bit < 8; ++bit)
      crc = (crc >> 1) ^ ((crc & 1) ? 0xedb88320 : 0);
  }
  return ~crc;
}

bool AllZero(const uint8_t* begin, const uint8_t* end) {
  return std::all_of(begin, end, [](uint8_t value) { return value == 0; });
}

}  // namespace

PersistentCommandJournal::PersistentCommandJournal(JournalStorage& storage)
    : storage_(storage) {
  std::array<uint8_t, RecordSize> data{};
  size_t length = 0;
  const JournalReadResult result = storage_.Read(data.data(), data.size(), length);
  if (result == JournalReadResult::Missing) {
    healthy_ = true;
    storageMissing_ = true;
  } else if (result == JournalReadResult::Found && Decode(data.data(), length, state_)) {
    healthy_ = true;
  }
}

bool PersistentCommandJournal::IsHealthy() const { return healthy_; }

PersistentCommandJournal::StateOrigin PersistentCommandJournal::Origin() const {
  return healthy_ ? state_.origin : StateOrigin::Unavailable;
}

uint64_t PersistentCommandJournal::CurrentEpoch() const {
  return healthy_ ? state_.currentEpoch : 0;
}

bool PersistentCommandJournal::HasPending() const {
  return !healthy_ || state_.pendingCompartment != 0;
}

bool PersistentCommandJournal::ReadHistoryEntry(size_t index, JournalHistoryEntry& entry) const {
  entry = {};
  if (!healthy_ || index >= state_.historyCount) return false;
  const auto& saved = state_.history[index];
  entry.identity = {saved.epoch, saved.id, saved.compartment, saved.fingerprint};
  entry.timedCompleted = saved.timedCompleted;
  entry.evidencePinned = saved.evidencePinned;
  return true;
}

bool PersistentCommandJournal::ExportRecord(std::array<uint8_t, RecordSize>& record) const {
  record.fill(0);
  if (!healthy_) return false;
  Encode(state_, record);
  return true;
}

bool PersistentCommandJournal::PreviewScopedIdentity(
    const std::string& deviceId, bool factoryProvisioning,
    std::array<uint8_t, RecordSize>& record) const {
  record.fill(0);
  State next;
  if (!BuildScopedState(deviceId, factoryProvisioning, next)) return false;
  Encode(next, record);
  return true;
}

bool PersistentCommandJournal::CanBeginOperation() const {
  return !HasPending() && state_.historyCount < HistoryCapacity;
}

uint8_t PersistentCommandJournal::PendingCompartment() const {
  return healthy_ ? state_.pendingCompartment : 0;
}

uint8_t PersistentCommandJournal::ReadyMask() const {
  return healthy_ ? state_.readyMask : 0;
}

bool PersistentCommandJournal::Begin(const std::string& commandId, uint8_t compartment) {
  return BeginWithIdentity(commandId, compartment, 0);
}

PersistentCommandJournal::Lookup PersistentCommandJournal::Find(
    const std::string& id, uint8_t compartment, uint64_t fingerprint) const {
  if (!healthy_) return Lookup::Unavailable;
  if (state_.currentEpoch != 0) return Lookup::StaleEpoch;
  return FindHistorical(0, id, compartment, fingerprint);
}

PersistentCommandJournal::Lookup PersistentCommandJournal::FindScoped(
    uint64_t epoch, const std::string& id, uint8_t compartment, uint64_t fingerprint) const {
  if (!healthy_) return Lookup::Unavailable;
  if (epoch == 0 || epoch != state_.currentEpoch) return Lookup::StaleEpoch;
  return FindHistorical(epoch, id, compartment, fingerprint);
}

PersistentCommandJournal::Lookup PersistentCommandJournal::FindHistorical(
    uint64_t epoch, const std::string& id, uint8_t compartment, uint64_t fingerprint) const {
  if (!healthy_) return Lookup::Unavailable;
  for (size_t index = 0; index < state_.historyCount; ++index) {
    const auto& entry = state_.history[index];
    if (entry.epoch == epoch && SameUuid(entry.id, id)) {
      return entry.compartment == compartment && entry.fingerprint == fingerprint
          ? Lookup::Same : Lookup::Conflict;
    }
  }
  return Lookup::Missing;
}

bool PersistentCommandJournal::BeginWithIdentity(
    const std::string& id, uint8_t compartment, uint64_t fingerprint) {
  if (state_.currentEpoch != 0) return false;
  return BeginAtEpoch(0, id, compartment, fingerprint);
}

bool PersistentCommandJournal::BeginScoped(
    uint64_t epoch, const std::string& id, uint8_t compartment, uint64_t fingerprint) {
  if (epoch == 0 || epoch != state_.currentEpoch || fingerprint == 0) return false;
  return BeginAtEpoch(epoch, id, compartment, fingerprint);
}

bool PersistentCommandJournal::BeginAtEpoch(
    uint64_t epoch, const std::string& id, uint8_t compartment, uint64_t fingerprint) {
  if (!CanBeginOperation() || !IsCanonicalUuid(id) || compartment < 1 || compartment > 6 ||
      FindHistorical(epoch, id, compartment, fingerprint) != Lookup::Missing ||
      (state_.readyMask & (1u << (compartment - 1))) == 0) {
    return false;
  }
  State next = state_;
  next.pendingCompartment = compartment;
  next.commandId = id;
  auto& entry = next.history[next.historyCount++];
  entry.id = id;
  entry.compartment = compartment;
  entry.fingerprint = fingerprint;
  entry.epoch = epoch;
  entry.evidencePinned = true;
  entry.timedCompleted = false;
  return Persist(next);
}

bool PersistentCommandJournal::Resolve(uint8_t readyMask) {
  if (!healthy_ || state_.pendingCompartment == 0 || readyMask > AllCompartmentsMask ||
      readyMask != static_cast<uint8_t>(state_.readyMask & ~(1u << (state_.pendingCompartment - 1)))) {
    return false;
  }
  State next = state_;
  next.commandId.clear();
  next.pendingCompartment = 0;
  next.readyMask = readyMask;
  return Persist(next);
}

bool PersistentCommandJournal::ResolveRestored(uint8_t compartment) {
  if (!healthy_ || state_.pendingCompartment == 0 || state_.pendingCompartment != compartment ||
      (state_.readyMask & (1u << (compartment - 1))) == 0) {
    return false;
  }
  State next = state_;
  next.commandId.clear();
  next.pendingCompartment = 0;
  return Persist(next);
}

bool PersistentCommandJournal::ConfirmTimedPresentation(
    const std::string& id, uint8_t compartment, uint64_t fingerprint) {
  return state_.currentEpoch == 0 && ConfirmTimedAtEpoch(0, id, compartment, fingerprint);
}

bool PersistentCommandJournal::ConfirmTimedPresentationScoped(
    uint64_t epoch, const std::string& id, uint8_t compartment, uint64_t fingerprint) {
  return epoch != 0 && epoch == state_.currentEpoch &&
         ConfirmTimedAtEpoch(epoch, id, compartment, fingerprint);
}

bool PersistentCommandJournal::ConfirmTimedAtEpoch(
    uint64_t epoch, const std::string& id, uint8_t compartment, uint64_t fingerprint) {
  if (HasPending() || fingerprint == 0) return false;
  State next = state_;
  for (size_t index = 0; index < next.historyCount; ++index) {
    auto& entry = next.history[index];
    if (entry.epoch == epoch && SameUuid(entry.id, id) &&
        entry.compartment == compartment && entry.fingerprint == fingerprint) {
      // Released evidence may be compacted: never create positive evidence
      // after its owner has already attested durable reconciliation.
      if (!entry.evidencePinned) return false;
      if (entry.timedCompleted) return true;
      entry.timedCompleted = true;
      return Persist(next);
    }
  }
  return false;
}

const PersistentCommandJournal::Entry* PersistentCommandJournal::ExactEntry(
    uint64_t epoch, const std::string& id, uint8_t compartment, uint64_t fingerprint) const {
  if (!healthy_) return nullptr;
  for (size_t index = 0; index < state_.historyCount; ++index) {
    const auto& entry = state_.history[index];
    if (entry.epoch == epoch && SameUuid(entry.id, id) && entry.compartment == compartment &&
        entry.fingerprint == fingerprint) return &entry;
  }
  return nullptr;
}

bool PersistentCommandJournal::WasTimedPresentationCompleted(
    const std::string& id, uint8_t compartment) const {
  if (!healthy_ || state_.currentEpoch != 0) return false;
  return WasLegacyTimedPresentationCompleted(id, compartment);
}

bool PersistentCommandJournal::WasLegacyTimedPresentationCompleted(
    const std::string& id, uint8_t compartment) const {
  if (!healthy_) return false;
  for (size_t index = 0; index < state_.historyCount; ++index) {
    const auto& entry = state_.history[index];
    if (entry.epoch == 0 && SameUuid(entry.id, id) && entry.compartment == compartment &&
        entry.fingerprint != 0 && entry.timedCompleted) return true;
  }
  return false;
}

bool PersistentCommandJournal::WasTimedPresentationCompletedScoped(
    uint64_t epoch, const std::string& id, uint8_t compartment, uint64_t fingerprint) const {
  // Historical epochs are intentional here: retained evidence serves old outbox
  // envelopes without authorizing a new physical operation.
  return epoch != 0 && WasTimedPresentationCompletedHistorical(epoch, id, compartment, fingerprint);
}

bool PersistentCommandJournal::WasTimedPresentationCompletedHistorical(
    uint64_t epoch, const std::string& id, uint8_t compartment, uint64_t fingerprint) const {
  if (fingerprint == 0) return false;
  const Entry* entry = ExactEntry(epoch, id, compartment, fingerprint);
  return entry && entry->timedCompleted;
}

bool PersistentCommandJournal::PersistReadyMask(uint8_t readyMask) {
  if (HasPending() || readyMask > AllCompartmentsMask) return false;
  State next = state_;
  next.readyMask = readyMask;
  return Persist(next);
}

bool PersistentCommandJournal::BuildScopedState(
    const std::string& deviceId, bool factoryProvisioning, State& next) const {
  if (HasPending() || state_.currentEpoch != 0 || !IsValidDeviceId(deviceId) ||
      (state_.origin == StateOrigin::Missing && !factoryProvisioning)) return false;
  next = state_;
  next.currentEpoch = 1;
  next.origin = StateOrigin::Scoped;
  next.deviceId = deviceId;
  // All legacy history, including unknown completion and positive evidence,
  // survives adoption until its exact durable consumers are reconciled.
  for (size_t index = 0; index < next.historyCount; ++index) next.history[index].evidencePinned = true;
  return true;
}

bool PersistentCommandJournal::EstablishScopedIdentity(
    const std::string& deviceId, bool factoryProvisioning) {
  State next;
  return BuildScopedState(deviceId, factoryProvisioning, next) && Persist(next);
}

bool PersistentCommandJournal::AdvanceEpochAndCompact() {
  if (HasPending() || state_.origin != StateOrigin::Scoped ||
      state_.currentEpoch == std::numeric_limits<uint64_t>::max()) return false;
  State next = state_;
  next.history = {};
  next.historyCount = 0;
  for (size_t index = 0; index < state_.historyCount; ++index) {
    if (state_.history[index].evidencePinned) next.history[next.historyCount++] = state_.history[index];
  }
  if (next.historyCount == HistoryCapacity) return false;
  ++next.currentEpoch;
  return Persist(next);
}

bool PersistentCommandJournal::ReleaseEvidencePin(
    uint64_t epoch, const std::string& id, uint8_t compartment, uint64_t fingerprint) {
  if (HasPending() || state_.origin != StateOrigin::Scoped) return false;
  State next = state_;
  for (size_t index = 0; index < next.historyCount; ++index) {
    auto& entry = next.history[index];
    if (entry.epoch == epoch && SameUuid(entry.id, id) && entry.compartment == compartment &&
        entry.fingerprint == fingerprint) {
      if (!entry.evidencePinned) return true;
      entry.evidencePinned = false;
      return Persist(next);
    }
  }
  return false;
}

bool PersistentCommandJournal::RetainEvidencePin(
    uint64_t epoch, const std::string& id, uint8_t compartment, uint64_t fingerprint) {
  if (HasPending() || state_.origin != StateOrigin::Scoped) return false;
  State next = state_;
  for (size_t index = 0; index < next.historyCount; ++index) {
    auto& entry = next.history[index];
    if (entry.epoch == epoch && SameUuid(entry.id, id) && entry.compartment == compartment &&
        entry.fingerprint == fingerprint) {
      if (entry.evidencePinned) return true;
      entry.evidencePinned = true;
      return Persist(next);
    }
  }
  return false;
}

bool PersistentCommandJournal::IsEvidencePinned(
    uint64_t epoch, const std::string& id, uint8_t compartment, uint64_t fingerprint) const {
  const Entry* entry = ExactEntry(epoch, id, compartment, fingerprint);
  return entry && entry->evidencePinned;
}

bool PersistentCommandJournal::Persist(const State& next) {
  std::array<uint8_t, RecordSize> data{};
  Encode(next, data);
  if (!storage_.Write(data.data(), data.size())) {
    healthy_ = false;
    return false;
  }
  state_ = next;
  storageMissing_ = false;
  return true;
}

bool PersistentCommandJournal::Decode(const uint8_t* data, size_t length, State& state) {
  if (length < 44 || !std::equal(Magic, Magic + sizeof(Magic), data)) return false;
  const uint8_t version = data[4];
  const bool version1 = version == 1 && length == 44;
  const bool legacyHistory = (version == 2 || version == 3) && length == LegacyRecordSize;
  const bool scopedRecord = version == RecordVersion && length == RecordSize;
  if ((!version1 && !legacyHistory && !scopedRecord) || data[5] > AllCompartmentsMask ||
      data[6] > 6 || (version1 ? data[7] != 0 : data[7] > HistoryCapacity)) return false;
  if (scopedRecord) {
    const size_t checksumOffset = RecordSize - 4;
    uint32_t expected = 0;
    for (unsigned byte = 0; byte < 4; ++byte)
      expected |= static_cast<uint32_t>(data[checksumOffset + byte]) << (8 * byte);
    if (Crc32(data, checksumOffset) != expected) return false;
  }

  State decoded;
  decoded.origin = StateOrigin::Legacy;
  decoded.readyMask = data[5];
  decoded.pendingCompartment = data[6];
  if (decoded.pendingCompartment == 0) {
    if (!AllZero(data + CommandIdOffset, data + 44)) return false;
  } else {
    decoded.commandId.assign(reinterpret_cast<const char*>(data + CommandIdOffset), CommandIdLength);
    if (!IsCanonicalUuid(decoded.commandId) ||
        (decoded.readyMask & (1u << (decoded.pendingCompartment - 1))) == 0) return false;
  }
  if (scopedRecord) {
    decoded.currentEpoch = Read64(data + 44);
    if (data[52] > static_cast<uint8_t>(StateOrigin::Scoped) || data[53] > MaximumDeviceIdLength) return false;
    decoded.origin = static_cast<StateOrigin>(data[52]);
    decoded.deviceId.assign(reinterpret_cast<const char*>(data + 54), data[53]);
    if (!AllZero(data + 54 + data[53], data + EntryOffset)) return false;
    if (decoded.origin == StateOrigin::Scoped) {
      if (decoded.currentEpoch == 0 || !IsValidDeviceId(decoded.deviceId)) return false;
    } else if (decoded.currentEpoch != 0 || !decoded.deviceId.empty()) return false;
  }

  if (version1) {
    // Old completed records never stored IDs; their evidence cannot be invented.
    if (decoded.pendingCompartment) {
      decoded.historyCount = 1;
      decoded.history[0].id = decoded.commandId;
      decoded.history[0].compartment = decoded.pendingCompartment;
    }
  } else {
    decoded.historyCount = data[7];
    const size_t start = scopedRecord ? EntryOffset : 44;
    const size_t stride = scopedRecord ? EntrySize : 45;
    for (size_t index = 0; index < HistoryCapacity; ++index) {
      const size_t offset = start + index * stride;
      if (index >= decoded.historyCount) {
        if (!AllZero(data + offset, data + offset + stride)) return false;
        continue;
      }
      auto& entry = decoded.history[index];
      entry.id.assign(reinterpret_cast<const char*>(data + offset), CommandIdLength);
      if (scopedRecord) {
        entry.compartment = data[offset + 36];
        const uint8_t flags = data[offset + 37];
        if ((flags & ~3u) != 0) return false;
        entry.timedCompleted = (flags & 1) != 0;
        entry.evidencePinned = (flags & 2) != 0;
        entry.fingerprint = Read64(data + offset + 38);
        entry.epoch = Read64(data + offset + 46);
        if (entry.epoch > decoded.currentEpoch ||
            (decoded.currentEpoch == 0 && !entry.evidencePinned)) return false;
      } else {
        entry.compartment = static_cast<uint8_t>(data[offset + 36] & (version == 3 ? 0x7f : 0xff));
        entry.timedCompleted = version == 3 && (data[offset + 36] & 0x80) != 0;
        entry.fingerprint = Read64(data + offset + 37);
      }
      if (!IsCanonicalUuid(entry.id) || entry.compartment < 1 || entry.compartment > 6 ||
          (entry.timedCompleted && entry.fingerprint == 0)) return false;
      for (size_t other = 0; other < index; ++other) {
        if (decoded.history[other].epoch == entry.epoch && SameUuid(decoded.history[other].id, entry.id)) return false;
      }
    }
    if (decoded.pendingCompartment) {
      bool found = false;
      for (size_t index = 0; index < decoded.historyCount; ++index) {
        const auto& entry = decoded.history[index];
        found |= entry.epoch == decoded.currentEpoch && SameUuid(entry.id, decoded.commandId) &&
                 entry.compartment == decoded.pendingCompartment && !entry.timedCompleted && entry.evidencePinned;
      }
      if (!found) return false;
    }
  }
  state = decoded;
  return true;
}

void PersistentCommandJournal::Encode(const State& state, std::array<uint8_t, RecordSize>& data) {
  std::copy(Magic, Magic + sizeof(Magic), data.begin());
  data[4] = RecordVersion;
  data[5] = state.readyMask;
  data[6] = state.pendingCompartment;
  data[7] = state.historyCount;
  if (state.pendingCompartment != 0)
    std::copy(state.commandId.begin(), state.commandId.end(), data.begin() + CommandIdOffset);
  Write64(data.data() + 44, state.currentEpoch);
  data[52] = static_cast<uint8_t>(state.origin);
  data[53] = static_cast<uint8_t>(state.deviceId.size());
  std::copy(state.deviceId.begin(), state.deviceId.end(), data.begin() + 54);
  for (size_t index = 0; index < state.historyCount; ++index) {
    const size_t offset = EntryOffset + index * EntrySize;
    const auto& entry = state.history[index];
    std::copy(entry.id.begin(), entry.id.end(), data.begin() + offset);
    data[offset + 36] = entry.compartment;
    data[offset + 37] = static_cast<uint8_t>((entry.timedCompleted ? 1 : 0) | (entry.evidencePinned ? 2 : 0));
    Write64(data.data() + offset + 38, entry.fingerprint);
    Write64(data.data() + offset + 46, entry.epoch);
  }
  const uint32_t checksum = Crc32(data.data(), RecordSize - 4);
  for (unsigned byte = 0; byte < 4; ++byte)
    data[RecordSize - 4 + byte] = static_cast<uint8_t>(checksum >> (8 * byte));
}

bool PersistentCommandJournal::IsCanonicalUuid(const std::string& value) {
  if (value.size() != CommandIdLength) return false;
  for (size_t index = 0; index < value.size(); ++index) {
    const bool hyphenPosition = index == 8 || index == 13 || index == 18 || index == 23;
    if (hyphenPosition ? value[index] != '-' : !IsHexDigit(value[index])) return false;
  }
  return true;
}

bool PersistentCommandJournal::IsValidDeviceId(const std::string& value) {
  // Matches DeviceIdentity's persisted device namespace without coupling this
  // pure storage engine to provisioning, service gates or ESP-IDF.
  if (value.empty() || value.size() > MaximumDeviceIdLength) return false;
  for (char character : value) {
    if (!((character >= 'a' && character <= 'z') || (character >= 'A' && character <= 'Z') ||
          (character >= '0' && character <= '9') || character == '-' || character == '_')) return false;
  }
  return true;
}

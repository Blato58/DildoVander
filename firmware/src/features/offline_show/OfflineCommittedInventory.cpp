#include "OfflineCommittedInventory.h"

#include <algorithm>
#include <cstddef>
#include <cstring>
#include <limits>

namespace kufr { namespace offline {
namespace {

constexpr uint8_t Magic[] = {'O', 'F', 'I', '1'};
constexpr size_t GenerationOffset = 8;
constexpr size_t DeviceLengthOffset = 16;
constexpr size_t DeviceOffset = 17;
constexpr size_t PayloadOffset = 81;

uint64_t Read64(const uint8_t* bytes) {
  uint64_t value = 0;
  for (unsigned byte = 0; byte < 8; ++byte) value |= static_cast<uint64_t>(bytes[byte]) << (8 * byte);
  return value;
}
void Write64(uint8_t* bytes, uint64_t value) {
  for (unsigned byte = 0; byte < 8; ++byte) bytes[byte] = static_cast<uint8_t>(value >> (8 * byte));
}
uint32_t Read32(const uint8_t* bytes) {
  uint32_t value = 0;
  for (unsigned byte = 0; byte < 4; ++byte) value |= static_cast<uint32_t>(bytes[byte]) << (8 * byte);
  return value;
}
void Write32(uint8_t* bytes, uint32_t value) {
  for (unsigned byte = 0; byte < 4; ++byte) bytes[byte] = static_cast<uint8_t>(value >> (8 * byte));
}
uint32_t Crc32(const uint8_t* bytes, size_t length) {
  uint32_t crc = 0xffffffffU;
  for (size_t index = 0; index < length; ++index) {
    crc ^= bytes[index];
    for (unsigned bit = 0; bit < 8; ++bit) crc = (crc >> 1) ^ ((crc & 1) ? 0xedb88320U : 0);
  }
  return ~crc;
}

bool ValidUuid(const std::array<char, 37>& id) {
  if (id[36] != 0) return false;
  for (size_t index = 0; index < 36; ++index) {
    const char value = id[index];
    if (index == 8 || index == 13 || index == 18 || index == 23) {
      if (value != '-') return false;
    } else if (!((value >= '0' && value <= '9') || (value >= 'a' && value <= 'f'))) return false;
  }
  return true;
}

template<class Box, class Record> bool ValidPodFlags(const uint8_t* bytes) {
  const std::array<size_t, 3> flags{{offsetof(Box, offlineEnabled), offsetof(Box, ambientEnabled), offsetof(Box, quiet)}};
  for (const auto offset : flags) if (bytes[offset] > 1) return false;
  const std::array<size_t, 3> recordFlags{{offsetof(Record, completed), offsetof(Record, scripted), offsetof(Record, shortVariant)}};
  for (size_t index = 0; index < 8; ++index) {
    for (const auto offset : recordFlags) {
      if (bytes[offsetof(Box, records) + index * sizeof(Record) + offset] > 1) return false;
    }
  }
  return true;
}

bool SameOperation(const Outcome& left, const Outcome& right) {
  if (left.operationEpoch != right.operationEpoch) return false;
  for (size_t index = 0; index < left.operationId.size(); ++index) {
    if (JournalIdentityLower(left.operationId[index]) != JournalIdentityLower(right.operationId[index])) return false;
  }
  return true;
}

void ClearInventory(JournalEvidenceInventory& inventory) {
  inventory.origin = JournalInventoryOrigin::Unavailable;
  inventory.deviceId.clear(); inventory.generation = 0; inventory.complete = false;
  inventory.ambiguous = false; inventory.count = 0;
  for (auto& reference : inventory.references) {
    reference.epoch = 0; reference.operationId.clear(); reference.compartment = 0; reference.fingerprint = 0;
  }
}

}  // namespace

static_assert(PayloadOffset + 4 + 3 + 16 == OfflineCommittedInventory::EnvelopeHeaderSize &&
    3 * 37 + 6 * 8 + 4 == OfflineCommittedInventory::OutcomeRecordSize, "Canonical envelope offsets match its fields");
static_assert(sizeof(Outcome) == 168 && offsetof(Outbox, records) == 24 && sizeof(Outbox) == 1368,
    "Legacy v2 POD layout must retain its original stride and bytes");
static_assert(OfflineCommittedInventory::MaximumRecordSize >= sizeof(Outbox) &&
    OfflineCommittedInventory::MaximumRecordSize >= sizeof(storage::LegacyOutboxV1), "Read capacity covers legacy blobs");

OfflineCommittedInventory::OfflineCommittedInventory(JournalStorage& storage) : storage_(storage) { Reopen(); }

bool OfflineCommittedInventory::ValidDeviceId(const std::string& deviceId) {
  if (deviceId.empty() || deviceId.size() > MaximumDeviceIdLength) return false;
  for (char value : deviceId) {
    if (!((value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z') ||
        (value >= '0' && value <= '9') || value == '-' || value == '_')) return false;
  }
  return true;
}

bool OfflineCommittedInventory::ValidRecords(const Outbox& outbox) {
  if (outbox.version != 2) return false;
  for (const auto& record : outbox.records) {
    // Preserve the legacy reader's eligibility checks. Missing associations are
    // inventory ambiguity, never authority to rewrite or discard old records.
    if (record.operationId[0] && (!ValidUuid(record.operationId) || !ValidUuid(record.bootId) ||
        !ValidUuid(record.localEveningId) || record.slot < 1 || record.slot > 6 ||
        (record.completed && record.completedAtMs < record.startedAtMs))) return false;
  }
  return true;
}

bool OfflineCommittedInventory::EmptyRecord(const Outcome& record) {
  for (char value : record.operationId) if (value) return false;
  for (char value : record.bootId) if (value) return false;
  for (char value : record.localEveningId) if (value) return false;
  return !record.generation && !record.startedAtMs && !record.completedAtMs && !record.slot &&
      !record.completed && !record.scripted && !record.shortVariant && !record.operationEpoch &&
      !record.physicalFingerprint && !record.deviceRevision;
}

bool OfflineCommittedInventory::AmbiguousRecord(const Outcome& record) {
  if (!record.operationId[0]) return !EmptyRecord(record);
  // Epoch-zero legacy rows have no captured physical association. Explicit
  // device binding cannot manufacture that association or positive completion.
  return !record.operationEpoch || !record.physicalFingerprint || !record.generation ||
      (!record.completed && record.completedAtMs != 0);
}

bool OfflineCommittedInventory::Reopen() {
  healthy_ = false; bound_ = false; origin_ = JournalInventoryOrigin::Unavailable;
  generation_ = 0; deviceId_.clear(); committed_ = {};
  size_t length = 0;
  const auto result = storage_.Read(bytes_.data(), bytes_.size(), length);
  if (result == JournalReadResult::Missing) {
    healthy_ = true; origin_ = JournalInventoryOrigin::Missing;
    return true;
  }
  if (result != JournalReadResult::Found || length > bytes_.size()) return false;
  if (length == EnvelopeSize) {
    if (!DecodeEnvelope(length)) return false;
  } else {
    const bool flags = length == sizeof(Outbox) ? ValidPodFlags<Outbox, Outcome>(bytes_.data()) :
        length == sizeof(storage::LegacyOutboxV1) &&
        ValidPodFlags<storage::LegacyOutboxV1, storage::LegacyOutcomeV1>(bytes_.data());
    if (!flags || !storage::DecodeOutboxBytes(bytes_.data(), length, committed_) || !ValidRecords(committed_)) return false;
  }
  healthy_ = true; origin_ = JournalInventoryOrigin::Valid;
  return true;
}

bool OfflineCommittedInventory::Read(Outbox& outbox) {
  if (!healthy_) return false;
  outbox = committed_;
  return true; // Legacy Missing still reads as an empty, UNBOUND outbox.
}

bool OfflineCommittedInventory::EstablishBinding(const std::string& deviceId, bool serviceAuthorized) {
  if (!serviceAuthorized || !healthy_ || !ValidDeviceId(deviceId)) return false;
  if (bound_) return deviceId_ == deviceId;
  return PersistBound(committed_, deviceId, 1);
}

bool OfflineCommittedInventory::Write(const Outbox& outbox) {
  if (!healthy_) return false;
  if (!ValidRecords(outbox) || (bound_ && generation_ == std::numeric_limits<uint64_t>::max())) {
    healthy_ = false;
    return false;
  }
  if (bound_) return PersistBound(outbox, deviceId_, generation_ + 1);
  // Existing production is deliberately unbound: keep its original v2 blob.
  if (!storage_.Write(reinterpret_cast<const uint8_t*>(&outbox), sizeof(outbox))) {
    healthy_ = false;
    return false;
  }
  committed_ = outbox;
  origin_ = JournalInventoryOrigin::Valid;
  return true;
}

bool OfflineCommittedInventory::PersistBound(
    const Outbox& outbox, const std::string& deviceId, uint64_t generation) {
  EncodeEnvelope(outbox, deviceId, generation);
  if (!storage_.Write(bytes_.data(), bytes_.size())) {
    healthy_ = false;
    return false;
  }
  committed_ = outbox; deviceId_ = deviceId; generation_ = generation;
  bound_ = true; healthy_ = true; origin_ = JournalInventoryOrigin::Valid;
  return true;
}

void OfflineCommittedInventory::EncodeEnvelope(
    const Outbox& outbox, const std::string& deviceId, uint64_t generation) {
  bytes_.fill(0);
  std::copy(Magic, Magic + sizeof(Magic), bytes_.begin());
  bytes_[4] = 1;
  Write64(bytes_.data() + GenerationOffset, generation);
  bytes_[DeviceLengthOffset] = static_cast<uint8_t>(deviceId.size());
  std::copy(deviceId.begin(), deviceId.end(), bytes_.begin() + DeviceOffset);
  size_t offset = PayloadOffset;
  Write32(bytes_.data() + offset, outbox.version); offset += 4;
  bytes_[offset++] = outbox.offlineEnabled ? 1 : 0;
  bytes_[offset++] = outbox.ambientEnabled ? 1 : 0;
  bytes_[offset++] = outbox.quiet ? 1 : 0;
  Write64(bytes_.data() + offset, outbox.serverSettingsRevision); offset += 8;
  Write64(bytes_.data() + offset, outbox.serverStopRevision); offset += 8;
  for (const auto& record : outbox.records) {
    for (const auto* id : {&record.operationId, &record.bootId, &record.localEveningId}) {
      std::copy(id->begin(), id->end(), bytes_.begin() + offset); offset += id->size();
    }
    Write64(bytes_.data() + offset, record.generation); offset += 8;
    Write64(bytes_.data() + offset, record.startedAtMs); offset += 8;
    Write64(bytes_.data() + offset, record.completedAtMs); offset += 8;
    bytes_[offset++] = record.slot;
    bytes_[offset++] = record.completed ? 1 : 0;
    bytes_[offset++] = record.scripted ? 1 : 0;
    bytes_[offset++] = record.shortVariant ? 1 : 0;
    Write64(bytes_.data() + offset, record.operationEpoch); offset += 8;
    Write64(bytes_.data() + offset, record.physicalFingerprint); offset += 8;
    Write64(bytes_.data() + offset, record.deviceRevision); offset += 8;
  }
  Write32(bytes_.data() + offset, Crc32(bytes_.data(), offset));
}

bool OfflineCommittedInventory::DecodeEnvelope(size_t length) {
  if (length != EnvelopeSize || !std::equal(Magic, Magic + sizeof(Magic), bytes_.begin()) ||
      bytes_[4] != 1 || bytes_[5] || bytes_[6] || bytes_[7] ||
      Read32(bytes_.data() + length - 4) != Crc32(bytes_.data(), length - 4)) return false;
  generation_ = Read64(bytes_.data() + GenerationOffset);
  const size_t deviceLength = bytes_[DeviceLengthOffset];
  if (!generation_ || !deviceLength || deviceLength > MaximumDeviceIdLength) return false;
  deviceId_.assign(reinterpret_cast<const char*>(bytes_.data() + DeviceOffset), deviceLength);
  if (!ValidDeviceId(deviceId_)) return false;
  for (size_t index = deviceLength; index < MaximumDeviceIdLength; ++index) {
    if (bytes_[DeviceOffset + index]) return false;
  }
  size_t offset = PayloadOffset;
  committed_.version = Read32(bytes_.data() + offset); offset += 4;
  if (bytes_[offset] > 1 || bytes_[offset + 1] > 1 || bytes_[offset + 2] > 1) return false;
  committed_.offlineEnabled = bytes_[offset++] != 0;
  committed_.ambientEnabled = bytes_[offset++] != 0;
  committed_.quiet = bytes_[offset++] != 0;
  committed_.serverSettingsRevision = Read64(bytes_.data() + offset); offset += 8;
  committed_.serverStopRevision = Read64(bytes_.data() + offset); offset += 8;
  for (auto& record : committed_.records) {
    for (auto* id : {&record.operationId, &record.bootId, &record.localEveningId}) {
      std::copy(bytes_.begin() + offset, bytes_.begin() + offset + id->size(), id->begin()); offset += id->size();
    }
    record.generation = Read64(bytes_.data() + offset); offset += 8;
    record.startedAtMs = Read64(bytes_.data() + offset); offset += 8;
    record.completedAtMs = Read64(bytes_.data() + offset); offset += 8;
    record.slot = bytes_[offset++];
    if (bytes_[offset] > 1 || bytes_[offset + 1] > 1 || bytes_[offset + 2] > 1) return false;
    record.completed = bytes_[offset++] != 0;
    record.scripted = bytes_[offset++] != 0;
    record.shortVariant = bytes_[offset++] != 0;
    record.operationEpoch = Read64(bytes_.data() + offset); offset += 8;
    record.physicalFingerprint = Read64(bytes_.data() + offset); offset += 8;
    record.deviceRevision = Read64(bytes_.data() + offset); offset += 8;
  }
  if (!ValidRecords(committed_)) return false;
  bound_ = true;
  return true;
}

bool OfflineCommittedInventory::ReadCommitted(JournalEvidenceInventory& inventory) const {
  ClearInventory(inventory);
  if (!healthy_) return false;
  inventory.origin = origin_;
  inventory.deviceId = deviceId_;
  inventory.generation = bound_ ? generation_ : 0;
  inventory.complete = bound_ && origin_ == JournalInventoryOrigin::Valid;
  inventory.ambiguous = !bound_ && origin_ == JournalInventoryOrigin::Valid;
  for (size_t index = 0; index < committed_.records.size(); ++index) {
    const auto& record = committed_.records[index];
    if (AmbiguousRecord(record)) inventory.ambiguous = true;
    if (!record.operationId[0]) continue;
    inventory.references[inventory.count++] = {record.operationEpoch, record.operationId.data(),
        record.slot, record.physicalFingerprint};
    for (size_t earlier = 0; earlier < index; ++earlier) {
      if (committed_.records[earlier].operationId[0] && SameOperation(record, committed_.records[earlier])) {
        inventory.ambiguous = true;
      }
    }
  }
  return true;
}

} }

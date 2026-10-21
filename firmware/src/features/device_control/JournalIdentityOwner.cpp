#include "JournalIdentityOwner.h"

#include <algorithm>

namespace {
constexpr uint8_t Magic[] = {'K', 'J', 'I', '1'};
constexpr uint8_t Version = 1;
constexpr size_t DeviceOffset = 16;

uint64_t Read64(const uint8_t* bytes) {
  uint64_t value = 0;
  for (unsigned index = 0; index < 8; ++index) value |= static_cast<uint64_t>(bytes[index]) << (index * 8);
  return value;
}
void Write64(uint8_t* bytes, uint64_t value) {
  for (unsigned index = 0; index < 8; ++index) bytes[index] = static_cast<uint8_t>(value >> (index * 8));
}
uint32_t Crc32(const uint8_t* bytes, size_t length) {
  uint32_t crc = 0xffffffff;
  for (size_t index = 0; index < length; ++index) {
    crc ^= bytes[index];
    for (unsigned bit = 0; bit < 8; ++bit)
      crc = (crc >> 1) ^ ((crc & 1) ? 0xedb88320 : 0);
  }
  return ~crc;
}
bool AllZero(const uint8_t* begin, const uint8_t* end) {
  return std::all_of(begin, end, [](uint8_t value) { return value == 0; });
}

// A bounded reader validates the embedded journal through its real codec. It
// cannot write storage, establish an identity or manufacture completion proof.
class ExpectedRecordStorage final : public JournalStorage {
 public:
  explicit ExpectedRecordStorage(const std::array<uint8_t, PersistentCommandJournal::RecordSize>& bytes)
      : bytes_(bytes) {}
  JournalReadResult Read(uint8_t* target, size_t capacity, size_t& length) override {
    if (capacity < bytes_.size()) return JournalReadResult::Failed;
    std::copy(bytes_.begin(), bytes_.end(), target);
    length = bytes_.size();
    return JournalReadResult::Found;
  }
  bool Write(const uint8_t*, size_t) override { return false; }
 private:
  const std::array<uint8_t, PersistentCommandJournal::RecordSize>& bytes_;
};
}

JournalIdentityOwner::JournalIdentityOwner(JournalStorage& markerStorage, PersistentCommandJournal& journal)
    : markerStorage_(markerStorage), journal_(journal) {
  std::array<uint8_t, MaximumRecordSize> bytes{};
  size_t length = 0;
  const auto result = markerStorage_.Read(bytes.data(), bytes.size(), length);
  if (result == JournalReadResult::Missing) {
    healthy_ = true;
  } else if (result == JournalReadResult::Found && Decode(bytes.data(), length, state_)) {
    healthy_ = true;
  }
}

bool JournalIdentityOwner::IsHealthy() const { return healthy_ && journal_.IsHealthy(); }

bool JournalIdentityOwner::HasEstablishedBinding(const JournalIdentityView& identity) const {
  return IsHealthy() && identity.healthy && identity.provisioned && ValidDeviceId(identity.deviceId) &&
      state_.phase == Phase::Established && state_.deviceId == identity.deviceId &&
      !journal_.IsStorageMissing() && journal_.Origin() == PersistentCommandJournal::StateOrigin::Scoped &&
      journal_.ScopedDeviceId() == state_.deviceId && journal_.CurrentEpoch() >= state_.initialEpoch;
}

JournalIdentityOwner::Validation JournalIdentityOwner::Validate(const JournalIdentityView& identity) const {
  if (!healthy_) return Validation::MarkerUnavailable;
  if (!identity.healthy) return Validation::IdentityUnavailable;
  if ((identity.provisioned && !ValidDeviceId(identity.deviceId)) ||
      (!identity.provisioned && !identity.deviceId.empty())) return Validation::IdentityMismatch;
  if (!journal_.IsHealthy()) return Validation::JournalUnavailable;
  if (identity.provisioned && journal_.IsStorageMissing()) return Validation::MissingJournal;

  if (state_.phase == Phase::Missing) {
    if (journal_.Origin() == PersistentCommandJournal::StateOrigin::Scoped) return Validation::MissingMarker;
    return journal_.HasPending() ? Validation::InspectionRequired : Validation::Unestablished;
  }
  if (identity.provisioned && identity.deviceId != state_.deviceId) return Validation::IdentityMismatch;
  if (!identity.provisioned && state_.phase != Phase::PreparingFactory) return Validation::IdentityUnavailable;
  if (journal_.IsStorageMissing()) {
    return state_.phase == Phase::PreparingFactory ? Validation::Preparing : Validation::MissingJournal;
  }
  if (journal_.HasPending()) return Validation::InspectionRequired;
  if (state_.phase != Phase::Established)
    return PreparationMatchesJournal() ? Validation::Preparing : Validation::IdentityMismatch;
  if (!identity.provisioned) return Validation::IdentityUnavailable;
  if (journal_.Origin() != PersistentCommandJournal::StateOrigin::Scoped ||
      journal_.ScopedDeviceId() != state_.deviceId || journal_.CurrentEpoch() < state_.initialEpoch)
    return Validation::IdentityMismatch;
  return Validation::Ready;
}

bool JournalIdentityOwner::BeginFactory(
    const std::string& target, const JournalIdentityView& identity, bool serviceAuthorized) {
  if (!serviceAuthorized || !IsHealthy() || state_.phase != Phase::Missing || !identity.healthy ||
      identity.provisioned || !identity.deviceId.empty() || !ValidDeviceId(target) ||
      !journal_.IsStorageMissing() || journal_.HasPending()) return false;
  State next;
  next.phase = Phase::PreparingFactory; next.deviceId = target; next.initialEpoch = 1;
  if (!journal_.PreviewScopedIdentity(target, true, next.expectedRecord)) return false;
  return Persist(next);
}

bool JournalIdentityOwner::BeginLegacy(const JournalIdentityView& identity, bool serviceAuthorized) {
  if (!serviceAuthorized || !IsHealthy() || state_.phase != Phase::Missing || !identity.healthy ||
      !identity.provisioned || !ValidDeviceId(identity.deviceId) || journal_.IsStorageMissing() ||
      journal_.Origin() == PersistentCommandJournal::StateOrigin::Scoped || journal_.HasPending()) return false;
  State next;
  next.phase = Phase::PreparingLegacy; next.deviceId = identity.deviceId; next.initialEpoch = 1;
  // A found, still-unbound Missing-origin record needs the stronger explicit
  // service authority; blob presence alone never changes the engine's guard.
  const bool bindUnestablished = journal_.Origin() == PersistentCommandJournal::StateOrigin::Missing;
  if (!journal_.PreviewScopedIdentity(next.deviceId, bindUnestablished, next.expectedRecord)) return false;
  return Persist(next);
}

bool JournalIdentityOwner::PreparationMatchesJournal() const {
  std::array<uint8_t, PersistentCommandJournal::RecordSize> record{};
  if (journal_.Origin() == PersistentCommandJournal::StateOrigin::Scoped) {
    return journal_.ExportRecord(record) && record == state_.expectedRecord;
  }
  if (state_.phase != Phase::PreparingLegacy || journal_.IsStorageMissing()) return false;
  const bool bindUnestablished = journal_.Origin() == PersistentCommandJournal::StateOrigin::Missing;
  return journal_.PreviewScopedIdentity(state_.deviceId, bindUnestablished, record) && record == state_.expectedRecord;
}

bool JournalIdentityOwner::ResumePreparation(const JournalIdentityView& identity, bool serviceAuthorized) {
  if (!serviceAuthorized || Validate(identity) != Validation::Preparing) return false;
  if (journal_.Origin() == PersistentCommandJournal::StateOrigin::Scoped) return PreparationMatchesJournal();
  if (state_.phase == Phase::PreparingFactory) {
    if (identity.provisioned || !journal_.IsStorageMissing()) return false;
  } else if (!PreparationMatchesJournal()) {
    return false;
  }
  const bool bindUnestablished = journal_.Origin() == PersistentCommandJournal::StateOrigin::Missing;
  if (!journal_.EstablishScopedIdentity(state_.deviceId,
      state_.phase == Phase::PreparingFactory || bindUnestablished)) {
    healthy_ = false; // A failed journal ACK may already have committed initialization.
    return false;
  }
  if (!PreparationMatchesJournal()) { healthy_ = false; return false; }
  return true;
}

bool JournalIdentityOwner::ValidInventory(const JournalEvidenceInventory& inventory, const std::string& deviceId) {
  if (inventory.origin != JournalInventoryOrigin::Valid || !inventory.complete ||
      inventory.deviceId != deviceId || !inventory.generation || inventory.count > inventory.Capacity) return false;
  for (size_t index = 0; index < inventory.count; ++index)
    if (!ValidJournalOperationIdentity(inventory.references[index])) return false;
  return true;
}

bool JournalIdentityOwner::Finalize(
    const JournalIdentityView& identity, const JournalEvidenceInventory& online,
    const JournalEvidenceInventory& offline, bool serviceAuthorized) {
  if (!serviceAuthorized || !identity.provisioned || Validate(identity) != Validation::Preparing ||
      journal_.Origin() != PersistentCommandJournal::StateOrigin::Scoped || !PreparationMatchesJournal() ||
      !ValidInventory(online, state_.deviceId) || !ValidInventory(offline, state_.deviceId)) return false;
  if (state_.phase == Phase::PreparingFactory &&
      (online.count || offline.count || online.ambiguous || offline.ambiguous)) return false;
  State next;
  next.phase = Phase::Established; next.deviceId = state_.deviceId; next.initialEpoch = state_.initialEpoch;
  return Persist(next);
}

bool JournalIdentityOwner::Persist(const State& next) {
  std::array<uint8_t, MaximumRecordSize> bytes{};
  const size_t length = Encode(next, bytes);
  if (!markerStorage_.Write(bytes.data(), length)) {
    healthy_ = false;
    return false;
  }
  state_ = next;
  return true;
}

size_t JournalIdentityOwner::Encode(const State& state, std::array<uint8_t, MaximumRecordSize>& bytes) {
  bytes.fill(0);
  std::copy(Magic, Magic + sizeof(Magic), bytes.begin());
  bytes[4] = Version; bytes[5] = static_cast<uint8_t>(state.phase);
  bytes[6] = static_cast<uint8_t>(state.deviceId.size());
  Write64(bytes.data() + 8, state.initialEpoch);
  std::copy(state.deviceId.begin(), state.deviceId.end(), bytes.begin() + DeviceOffset);
  const bool preparing = state.phase != Phase::Established;
  if (preparing) std::copy(state.expectedRecord.begin(), state.expectedRecord.end(), bytes.begin() + MarkerHeaderSize);
  const size_t length = preparing ? PreparingRecordSize : EstablishedRecordSize;
  const uint32_t crc = Crc32(bytes.data(), length - 4);
  for (unsigned index = 0; index < 4; ++index) bytes[length - 4 + index] = static_cast<uint8_t>(crc >> (index * 8));
  return length;
}

bool JournalIdentityOwner::Decode(const uint8_t* bytes, size_t length, State& state) {
  if ((length != PreparingRecordSize && length != EstablishedRecordSize) ||
      !std::equal(Magic, Magic + sizeof(Magic), bytes) || bytes[4] != Version || bytes[7] != 0 ||
      bytes[5] < static_cast<uint8_t>(Phase::PreparingFactory) || bytes[5] > static_cast<uint8_t>(Phase::Established) ||
      bytes[6] == 0 || bytes[6] > PersistentCommandJournal::MaximumDeviceIdLength) return false;
  uint32_t expectedCrc = 0;
  for (unsigned index = 0; index < 4; ++index)
    expectedCrc |= static_cast<uint32_t>(bytes[length - 4 + index]) << (index * 8);
  if (Crc32(bytes, length - 4) != expectedCrc ||
      !AllZero(bytes + DeviceOffset + bytes[6], bytes + MarkerHeaderSize)) return false;
  State decoded;
  decoded.phase = static_cast<Phase>(bytes[5]); decoded.initialEpoch = Read64(bytes + 8);
  decoded.deviceId.assign(reinterpret_cast<const char*>(bytes + DeviceOffset), bytes[6]);
  if (!decoded.initialEpoch || !ValidDeviceId(decoded.deviceId)) return false;
  if (decoded.phase == Phase::Established) {
    if (length != EstablishedRecordSize) return false;
  } else {
    if (length != PreparingRecordSize || decoded.initialEpoch != 1) return false;
    std::copy(bytes + MarkerHeaderSize, bytes + MarkerHeaderSize + decoded.expectedRecord.size(), decoded.expectedRecord.begin());
    ExpectedRecordStorage storage(decoded.expectedRecord);
    PersistentCommandJournal expected(storage);
    std::array<uint8_t, PersistentCommandJournal::RecordSize> canonical{};
    if (!expected.IsHealthy() || expected.Origin() != PersistentCommandJournal::StateOrigin::Scoped ||
        expected.HasPending() || expected.ScopedDeviceId() != decoded.deviceId ||
        expected.CurrentEpoch() != decoded.initialEpoch || !expected.ExportRecord(canonical) ||
        canonical != decoded.expectedRecord) return false;
    if (decoded.phase == Phase::PreparingFactory && (expected.HistoryCount() || expected.ReadyMask())) return false;
    if (decoded.phase == Phase::PreparingLegacy) {
      for (size_t index = 0; index < expected.HistoryCount(); ++index) {
        JournalHistoryEntry entry;
        if (!expected.ReadHistoryEntry(index, entry) || entry.identity.epoch || !entry.evidencePinned) return false;
      }
    }
  }
  state = decoded;
  return true;
}

bool JournalIdentityOwner::ValidDeviceId(const std::string& deviceId) {
  if (deviceId.empty() || deviceId.size() > PersistentCommandJournal::MaximumDeviceIdLength) return false;
  for (char value : deviceId) {
    if (!((value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z') ||
          (value >= '0' && value <= '9') || value == '-' || value == '_')) return false;
  }
  return true;
}

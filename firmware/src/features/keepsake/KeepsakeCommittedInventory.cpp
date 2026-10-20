#include "KeepsakeCommittedInventory.h"

#include <cstring>
#include <limits>
#include <vector>

#include "KeepsakePersistenceCodec.h"
#include "../device_control/PersistentCommandJournal.h"

namespace {
uint32_t Crc32(const std::string& bytes) {
  uint32_t crc = 0xffffffff;
  for (unsigned char byte : bytes) {
    crc ^= byte;
    for (unsigned bit = 0; bit < 8; ++bit) crc = (crc >> 1) ^ ((crc & 1) ? 0xedb88320 : 0);
  }
  return ~crc;
}
std::string Checksum(const std::string& bytes) {
  const uint32_t crc = Crc32(bytes);
  const char* digits = "0123456789abcdef";
  std::string value(8, '0');
  for (unsigned index = 0; index < 8; ++index) value[7 - index] = digits[(crc >> (index * 4)) & 15];
  return value;
}
bool UniqueFields(cJSON* node, unsigned depth = 0) {
  if (!node || depth > 8) return false;
  for (auto* item = node->child; item; item = item->next) {
    if (cJSON_IsObject(node)) {
      if (!item->string) return false;
      for (auto* previous = node->child; previous != item; previous = previous->next)
        if (previous->string && std::strcmp(previous->string, item->string) == 0) return false;
    }
    if ((cJSON_IsObject(item) || cJSON_IsArray(item)) && !UniqueFields(item, depth + 1)) return false;
  }
  return true;
}
bool Related(const KeepsakeDisposition& disposition, const KeepsakeOutcome& outcome) {
  if (disposition.operationEpoch != outcome.operationEpoch) return false;
  if (disposition.operationId == outcome.operationId || disposition.outcomeId == outcome.outcomeId) return true;
  if (disposition.physicalOperationId.empty()
      || disposition.physicalOperationId.size() != outcome.physicalOperationId.size()) return false;
  for (size_t index = 0; index < disposition.physicalOperationId.size(); ++index)
    if (JournalIdentityLower(disposition.physicalOperationId[index])
        != JournalIdentityLower(outcome.physicalOperationId[index])) return false;
  return true; // Different backend identities cannot hide one physical UUID's dependencies.
}
}

KeepsakeCommittedInventory::KeepsakeCommittedInventory(JournalStorage& storage) : storage_(storage) { Reopen(); }

bool KeepsakeCommittedInventory::ReadBytes(std::string& bytes, bool& missing) const {
  std::vector<uint8_t> buffer(MaximumRecordSize);
  size_t length = 0;
  const auto result = storage_.Read(buffer.data(), buffer.size(), length);
  missing = result == JournalReadResult::Missing;
  if (missing) { bytes.clear(); return true; }
  if (result != JournalReadResult::Found || !length || length > buffer.size()) { healthy_ = false; return false; }
  bytes.assign(reinterpret_cast<const char*>(buffer.data()), length);
  if (!bytes.empty() && bytes.back() == '\0') bytes.pop_back();
  if (bytes.empty() || bytes.find('\0') != std::string::npos) { healthy_ = false; return false; }
  return true;
}

bool KeepsakeCommittedInventory::Reopen() {
  healthy_ = false;
  std::string bytes, payload, deviceId;
  bool missing = false, bound = false;
  uint64_t generation = 0;
  KeepsakeOutboxState state, decoded;
  if (!ReadBytes(bytes, missing)) return false;
  if (!missing) {
    auto* root = cJSON_ParseWithLengthOpts(bytes.c_str(), bytes.size() + 1, nullptr, true);
    if (!root) return false;
    uint64_t schema = 0;
    bound = cJSON_IsObject(root) && keepsake_persistence::Number(root, "schemaVersion", schema) && schema == 4;
    const bool bindingFields = cJSON_GetObjectItemCaseSensitive(root, "deviceId")
        || cJSON_GetObjectItemCaseSensitive(root, "inventoryGeneration")
        || cJSON_GetObjectItemCaseSensitive(root, "payload") || cJSON_GetObjectItemCaseSensitive(root, "crc32");
    cJSON_Delete(root);
    if (bindingFields && !bound) return false; // Corrupt bound data cannot downgrade itself to legacy.
    if (bound) {
      if (!DecodeBound(bytes, state, deviceId, generation)) return false;
    } else if (!keepsake_persistence::Decode(bytes.c_str(), bytes.size() + 1, state)) {
      return false;
    }
  }
  if (!ValidState(state, decoded, payload)) return false;
  committedBytes_ = bytes; committedPayload_ = payload; committedState_ = std::move(decoded);
  deviceId_ = deviceId; generation_ = generation; missing_ = missing; bound_ = bound; healthy_ = true;
  return true;
}

bool KeepsakeCommittedInventory::CheckCommittedBytes() const {
  if (!healthy_) return false;
  std::string bytes;
  bool missing = false;
  if (!ReadBytes(bytes, missing) || missing != missing_ || bytes != committedBytes_) {
    healthy_ = false;
    return false;
  }
  return true;
}

bool KeepsakeCommittedInventory::ReadState(KeepsakeOutboxState& state) const {
  state = {};
  if (!CheckCommittedBytes()) return false;
  state = committedState_;
  return true;
}

bool KeepsakeCommittedInventory::ValidState(
    const KeepsakeOutboxState& state, KeepsakeOutboxState& decoded, std::string& payload) {
  payload = keepsake_persistence::Encode(state);
  if (payload.size() + 1 > MaximumRecordSize || !keepsake_persistence::Decode(payload.c_str(), payload.size() + 1, decoded)) return false;
  KeepsakeOutbox validator;
  validator.RetainRetirementLedger();
  return validator.Restore(decoded.pending, decoded.quarantined, decoded.prepared);
}

bool KeepsakeCommittedInventory::RetainsLedger(const KeepsakeOutboxState& state) const {
  for (const auto& saved : committedState_.quarantined) {
    bool retained = false;
    for (const auto& candidate : state.quarantined) retained |= SameKeepsakeDisposition(saved, candidate);
    if (!retained) return false;
  }
  return true;
}

bool KeepsakeCommittedInventory::PreservesActiveReferences(const KeepsakeOutboxState& state) const {
  const auto retained = [&](const KeepsakeOutcome& saved) {
    for (const auto& candidate : state.pending) if (SameKeepsakeCandidate(saved, candidate)) return true;
    for (const auto& candidate : state.prepared) if (SameKeepsakeCandidate(saved, candidate)) return true;
    for (const auto& receipt : state.quarantined) if (ExactKeepsakeDisposition(receipt, saved)) return true;
    return false;
  };
  for (const auto& saved : committedState_.pending) if (!retained(saved)) return false;
  for (const auto& saved : committedState_.prepared) if (!retained(saved)) return false;
  return true;
}

bool KeepsakeCommittedInventory::Commit(const KeepsakeOutboxState& state) { return CommitState(state, false); }

bool KeepsakeCommittedInventory::CommitState(const KeepsakeOutboxState& state, bool pruning) {
  if (!CheckCommittedBytes()) return false;
  KeepsakeOutboxState decoded;
  std::string payload;
  if (!ValidState(state, decoded, payload)
      || (bound_ && !pruning && !RetainsLedger(decoded))
      || (bound_ && !PreservesActiveReferences(decoded))) return false;
  if (payload == committedPayload_) return true; // A missing empty blob remains Missing.
  if (bound_ && generation_ == std::numeric_limits<uint64_t>::max()) { healthy_ = false; return false; }
  const uint64_t nextGeneration = bound_ ? generation_ + 1 : 0;
  const std::string bytes = bound_ ? EncodeBound(decoded, deviceId_, nextGeneration) : payload;
  return Write(decoded, payload, bytes, bound_, deviceId_, nextGeneration);
}

bool KeepsakeCommittedInventory::PruneRetired(
    const PersistentCommandJournal& journal, JournalReconciliationBarrier& barrier) {
  if (!barrier.TryEnter()) return false;
  struct BarrierLease {
    JournalReconciliationBarrier& barrier;
    ~BarrierLease() { barrier.Leave(); }
  } lease{barrier};
  if (!IsBound() || !CheckCommittedBytes() || !journal.IsHealthy() || journal.HasPending()
      || journal.Origin() != PersistentCommandJournal::StateOrigin::Scoped || journal.ScopedDeviceId() != deviceId_)
    return false;
  KeepsakeOutboxState next = committedState_;
  next.quarantined.erase(std::remove_if(next.quarantined.begin(), next.quarantined.end(), [&](const KeepsakeDisposition& entry) {
    const auto identity = KeepsakePhysicalIdentity(entry);
    if (entry.reason == "physical_outcome_unknown" || !identity.fingerprint || !ValidJournalOperationIdentity(identity)
        || identity.epoch >= journal.CurrentEpoch()
        || journal.FindHistorical(identity.epoch, identity.operationId, identity.compartment, identity.fingerprint)
            != CommandJournal::Lookup::Missing) return false;
    const auto depends = [&](const KeepsakeOutcome& candidate) {
      if (Related(entry, candidate)) return true;
      const auto live = KeepsakePhysicalIdentity(candidate);
      if (identity.epoch != live.epoch || identity.operationId.size() != live.operationId.size()) return false;
      for (size_t index = 0; index < identity.operationId.size(); ++index)
        if (JournalIdentityLower(identity.operationId[index]) != JournalIdentityLower(live.operationId[index])) return false;
      return true; // A matching or conflicting physical UUID is still a dependency.
    };
    for (const auto& item : committedState_.pending) if (depends(item)) return false;
    for (const auto& item : committedState_.prepared) if (depends(item)) return false;
    return true;
  }), next.quarantined.end());
  return CommitState(next, true);
}

bool KeepsakeCommittedInventory::Establish(const std::string& deviceId, bool serviceAuthorized) {
  if (!serviceAuthorized || !ValidDeviceId(deviceId) || !CheckCommittedBytes()) return false;
  if (bound_) return deviceId == deviceId_;
  const std::string bytes = EncodeBound(committedState_, deviceId, 1);
  return Write(committedState_, committedPayload_, bytes, true, deviceId, 1);
}

bool KeepsakeCommittedInventory::Write(
    const KeepsakeOutboxState& state, const std::string& payload, const std::string& bytes,
    bool bound, const std::string& deviceId, uint64_t generation) {
  if (bytes.size() + 1 > MaximumRecordSize) return false;
  if (!storage_.Write(reinterpret_cast<const uint8_t*>(bytes.c_str()), bytes.size() + 1)) {
    healthy_ = false;
    return false;
  }
  // Only a successful ACK advances this cache. A lost ACK requires Reopen even
  // when the new bytes actually reached the storage port.
  committedState_ = state; committedPayload_ = payload; committedBytes_ = bytes;
  deviceId_ = deviceId; generation_ = generation; missing_ = false; bound_ = bound;
  return true;
}

std::string KeepsakeCommittedInventory::EncodeBound(
    const KeepsakeOutboxState& state, const std::string& deviceId, uint64_t generation) {
  const std::string prefix = "{\"schemaVersion\":4,\"deviceId\":\"" + deviceId
      + "\",\"inventoryGeneration\":\"" + std::to_string(generation) + "\",\"payload\":"
      + keepsake_persistence::Encode(state);
  return prefix + ",\"crc32\":\"" + Checksum(prefix) + "\"}";
}

bool KeepsakeCommittedInventory::DecodeBound(
    const std::string& bytes, KeepsakeOutboxState& state, std::string& deviceId, uint64_t& generation) {
  auto* root = cJSON_ParseWithLengthOpts(bytes.c_str(), bytes.size() + 1, nullptr, true);
  std::string decimal, crc;
  auto* payload = cJSON_GetObjectItemCaseSensitive(root, "payload");
  uint64_t schema = 0;
  bool valid = cJSON_IsObject(root) && cJSON_GetArraySize(root) == 5 && UniqueFields(root)
      && keepsake_persistence::Number(root, "schemaVersion", schema) && schema == 4
      && keepsake_persistence::Text(root, "deviceId", deviceId, 64) && ValidDeviceId(deviceId)
      && keepsake_persistence::Text(root, "inventoryGeneration", decimal, 20) && ParseOperationEpoch(decimal, generation) && generation
      && keepsake_persistence::Text(root, "crc32", crc, 8) && crc.size() == 8 && cJSON_IsObject(payload);
  char* payloadBytes = valid ? cJSON_PrintUnformatted(payload) : nullptr;
  valid = valid && payloadBytes && keepsake_persistence::Decode(payloadBytes, std::strlen(payloadBytes) + 1, state);
  cJSON_free(payloadBytes); cJSON_Delete(root);
  // Canonical comparison also covers CRC, decimal strings, unknown fields and
  // padding. A bound payload never silently normalizes changed durable bytes.
  return valid && bytes == EncodeBound(state, deviceId, generation);
}

bool KeepsakeCommittedInventory::ValidDeviceId(const std::string& deviceId) {
  if (deviceId.empty() || deviceId.size() > 64) return false;
  for (char value : deviceId) {
    if (!((value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z') ||
          (value >= '0' && value <= '9') || value == '-' || value == '_')) return false;
  }
  return true;
}

bool KeepsakeCommittedInventory::AddReference(
    JournalEvidenceInventory& inventory, const JournalOperationIdentity& identity) {
  if (!identity.fingerprint || !ValidJournalOperationIdentity(identity)) inventory.ambiguous = true;
  if (!ValidJournalOperationIdentity(identity)) return true;
  for (size_t index = 0; index < inventory.count; ++index)
    if (SameJournalOperationIdentity(inventory.references[index], identity)) return true;
  if (inventory.count >= inventory.Capacity) return false;
  inventory.references[inventory.count++] = identity;
  return true;
}

bool KeepsakeCommittedInventory::ReadCommitted(JournalEvidenceInventory& inventory) const {
  inventory = {};
  if (!CheckCommittedBytes()) return false;
  if (missing_) { inventory.origin = JournalInventoryOrigin::Missing; return true; }
  inventory.origin = JournalInventoryOrigin::Valid;
  if (!bound_) { inventory.ambiguous = true; return true; }
  inventory.deviceId = deviceId_; inventory.generation = generation_; inventory.complete = true;
  for (const auto& item : committedState_.pending)
    if (!AddReference(inventory, KeepsakePhysicalIdentity(item))) { healthy_ = false; inventory = {}; return false; }
  for (const auto& item : committedState_.prepared)
    if (!AddReference(inventory, KeepsakePhysicalIdentity(item))) { healthy_ = false; inventory = {}; return false; }
  for (const auto& disposition : committedState_.quarantined) {
    const auto identity = KeepsakePhysicalIdentity(disposition);
    const bool exact = identity.fingerprint && ValidJournalOperationIdentity(identity);
    bool related = false, conflict = false;
    for (const auto& item : committedState_.pending) {
      if (!Related(disposition, item)) continue;
      related = true; conflict |= !ExactKeepsakeDisposition(disposition, item);
    }
    for (const auto& item : committedState_.prepared) {
      if (!Related(disposition, item)) continue;
      related = true; conflict |= !ExactKeepsakeDisposition(disposition, item);
    }
    const bool unknown = disposition.reason == "physical_outcome_unknown";
    if (!exact || conflict || unknown) inventory.ambiguous = true;
    // A terminal ledger entry can cease being a live dependency only after
    // BOTH pending/prepared removals are committed. It never shadows a conflict.
    if (related || !exact || unknown) {
      if (!AddReference(inventory, identity)) { healthy_ = false; inventory = {}; return false; }
    }
  }
  return true;
}

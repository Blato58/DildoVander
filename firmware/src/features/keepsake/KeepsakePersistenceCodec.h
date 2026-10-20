#pragma once

#include <cmath>
#include <cstring>
#include "cJSON.h"
#include "KeepsakeOutbox.h"
#include "../device_control/OperationEpoch.h"
#include "../conversation/ConversationStartCodec.h"

// Shared production NVS codec, exercised by host serialized-crash fixtures.
namespace keepsake_persistence {
constexpr size_t MaximumBytes = 32768;
inline bool Valid(const KeepsakeOutcome& item) {
  return IsConversationUuid(item.operationId) && IsConversationUuid(item.outcomeId)
      && IsConversationUuid(item.proposalId) && IsConversationUuid(item.physicalOperationId)
      && IsConversationUuid(item.bootId) && item.generation <= INT32_MAX && item.deviceRevision <= INT32_MAX
      && (!item.operationEpoch || item.physicalFingerprint);
}

inline std::string Body(const KeepsakeOutcome& item, bool persistent) {
  return "{\"schemaVersion\":1," + std::string(persistent ? "\"operationId\":\"" + item.operationId + "\"," : "")
      + "\"outcomeId\":\"" + item.outcomeId + "\",\"proposalId\":\"" + item.proposalId
      + "\",\"physicalOperationId\":\"" + item.physicalOperationId + "\",\"bootId\":\"" + item.bootId
      + "\",\"generation\":" + std::to_string(item.generation) + ",\"deviceRevision\":"
      + std::to_string(item.deviceRevision) + (persistent ? ",\"compartment\":" + std::to_string(item.compartment) : "")
      + (item.operationEpoch ? ",\"operationEpoch\":\"" + std::to_string(item.operationEpoch) + "\"" : "")
      + (item.physicalFingerprint ? ",\"physicalFingerprint\":\"" + std::to_string(item.physicalFingerprint) + "\"" : "")
      + ",\"presentationEvidence\":\"completed_by_timing\"}";
}

inline bool Text(cJSON* root, const char* name, std::string& target, size_t maximum = 36) {
  auto* value = cJSON_GetObjectItemCaseSensitive(root, name);
  if (!cJSON_IsString(value) || !value->valuestring || std::strlen(value->valuestring) > maximum) return false;
  target = value->valuestring;
  return true;
}

inline bool Number(cJSON* root, const char* name, uint64_t& target) {
  auto* value = cJSON_GetObjectItemCaseSensitive(root, name);
  if (!cJSON_IsNumber(value) || !std::isfinite(value->valuedouble) || value->valuedouble < 0
      || value->valuedouble > INT32_MAX || std::floor(value->valuedouble) != value->valuedouble) return false;
  target = static_cast<uint64_t>(value->valuedouble);
  return true;
}

// Missing fields are immutable legacy zero, never the current device epoch.
inline bool OptionalIdentityNumber(cJSON* root, const char* name, uint64_t& target) {
  auto* value = cJSON_GetObjectItemCaseSensitive(root, name);
  if (!value) { target = 0; return true; }
  return cJSON_IsString(value) && value->valuestring && ParseOperationEpoch(value->valuestring, target);
}

inline bool Identity(cJSON* root, KeepsakeOutcome& item) {
  return OptionalIdentityNumber(root, "operationEpoch", item.operationEpoch)
      && OptionalIdentityNumber(root, "physicalFingerprint", item.physicalFingerprint);
}

inline bool UniqueIdentityFields(cJSON* object) {
  const char* names[] = {"operationEpoch", "physicalFingerprint", "physicalOperationId", "compartment"};
  for (const char* name : names) {
    unsigned count = 0;
    for (auto* item = object ? object->child : nullptr; item; item = item->next)
      if (item->string && std::strcmp(item->string, name) == 0 && ++count > 1) return false;
  }
  return true;
}

inline bool DispositionIdentity(cJSON* root, KeepsakeDisposition& item) {
  if (!UniqueIdentityFields(root) || !OptionalIdentityNumber(root, "physicalFingerprint", item.physicalFingerprint)) return false;
  if (cJSON_GetObjectItemCaseSensitive(root, "physicalOperationId")
      && (!Text(root, "physicalOperationId", item.physicalOperationId) || !IsConversationUuid(item.physicalOperationId))) return false;
  uint64_t compartment = 0;
  if (cJSON_GetObjectItemCaseSensitive(root, "compartment")
      && (!Number(root, "compartment", compartment) || compartment > 6)) return false;
  item.compartment = static_cast<uint8_t>(compartment);
  return true;
}

inline bool MatchesOutcomeAcknowledgement(const char* bytes, size_t length, const KeepsakeOutcome& outcome) {
  auto* root = cJSON_ParseWithLength(bytes, length);
  std::string locator, resultOutcome, physical;
  uint64_t epoch = 0, operationEpoch = 0, fingerprint = 0;
  const bool matching = Text(root, "locator", locator) && IsConversationUuid(locator)
      && Text(root, "outcomeId", resultOutcome) && resultOutcome == outcome.outcomeId
      && Text(root, "physicalOperationId", physical) && physical == outcome.physicalOperationId
      && Number(root, "sessionEpoch", epoch) && epoch == outcome.generation
      && OptionalIdentityNumber(root, "operationEpoch", operationEpoch) && operationEpoch == outcome.operationEpoch
      && OptionalIdentityNumber(root, "physicalFingerprint", fingerprint) && fingerprint == outcome.physicalFingerprint;
  cJSON_Delete(root);
  return matching;
}

inline std::string Encode(const KeepsakeOutboxState& state) {
  std::string bytes = "{\"schemaVersion\":3,\"pending\":[";
  for (size_t i = 0; i < state.pending.size(); ++i) {
    if (i) bytes += ',';
    bytes += Body(state.pending[i], true);
  }
  bytes += "],\"quarantined\":[";
  for (size_t i = 0; i < state.quarantined.size(); ++i) {
    if (i) bytes += ',';
    const auto& item = state.quarantined[i];
    bytes += "{\"operationId\":\"" + item.operationId + "\",\"outcomeId\":\"" + item.outcomeId
        + "\",\"reason\":\"" + item.reason + "\""
        + (item.operationEpoch ? ",\"operationEpoch\":\"" + std::to_string(item.operationEpoch) + "\"" : "")
        + (item.physicalOperationId.empty() ? "" : ",\"physicalOperationId\":\"" + item.physicalOperationId + "\"")
        + (!item.physicalOperationId.empty() || item.compartment ? ",\"compartment\":" + std::to_string(item.compartment) : "")
        + (!item.physicalOperationId.empty() || item.physicalFingerprint ? ",\"physicalFingerprint\":\"" + std::to_string(item.physicalFingerprint) + "\"" : "") + "}";
  }
  bytes += "],\"prepared\":[";
  for (size_t i = 0; i < state.prepared.size(); ++i) {
    if (i) bytes += ',';
    bytes += Body(state.prepared[i], true);
  }
  bytes += "]}";
  return bytes;
}

inline bool Decode(const char* bytes, size_t length, KeepsakeOutboxState& state) {
  if (!bytes || length == 0 || length > MaximumBytes) return false;
  auto* root = cJSON_ParseWithLength(bytes, length);
  auto* pending = cJSON_IsArray(root) ? root : cJSON_GetObjectItemCaseSensitive(root, "pending");
  auto* quarantined = cJSON_GetObjectItemCaseSensitive(root, "quarantined");
  auto* prepared = cJSON_GetObjectItemCaseSensitive(root, "prepared");
  std::vector<KeepsakeOutcome> outcomes;
  std::vector<KeepsakeDisposition> dispositions;
  std::vector<KeepsakeOutcome> preparations;
  uint64_t version = 0;
  bool valid = (cJSON_IsArray(root) || (Number(root, "schemaVersion", version) && (version == 1 || version == 2 || version == 3) && cJSON_IsArray(quarantined)))
      && cJSON_IsArray(pending) && cJSON_GetArraySize(pending) <= static_cast<int>(KeepsakeOutbox::Capacity);
  for (int i = 0; valid && i < cJSON_GetArraySize(pending); ++i) {
    auto* entry = cJSON_GetArrayItem(pending, i);
    KeepsakeOutcome item;
    valid = UniqueIdentityFields(entry) && Text(entry, "operationId", item.operationId) && Text(entry, "outcomeId", item.outcomeId)
        && Text(entry, "proposalId", item.proposalId) && Text(entry, "physicalOperationId", item.physicalOperationId)
        && Text(entry, "bootId", item.bootId) && Number(entry, "generation", item.generation)
        && Number(entry, "deviceRevision", item.deviceRevision) && Identity(entry, item) && Valid(item);
    uint64_t compartment = 0;
    if (cJSON_GetObjectItemCaseSensitive(entry, "compartment")) {
      valid = valid && Number(entry, "compartment", compartment) && compartment <= 6;
      item.compartment = static_cast<uint8_t>(compartment);
    }
    if (item.operationEpoch && (item.compartment < 1 || item.compartment > 6)) valid = false;
    if (valid) outcomes.push_back(std::move(item));
  }
  if (quarantined && cJSON_GetArraySize(quarantined) > static_cast<int>(KeepsakeOutbox::QuarantineCapacity)) valid = false;
  for (int i = 0; valid && quarantined && i < cJSON_GetArraySize(quarantined); ++i) {
    auto* entry = cJSON_GetArrayItem(quarantined, i);
    KeepsakeDisposition disposition;
    valid = Text(entry, "operationId", disposition.operationId) && IsConversationUuid(disposition.operationId)
        && Text(entry, "outcomeId", disposition.outcomeId) && IsConversationUuid(disposition.outcomeId)
        && OptionalIdentityNumber(entry, "operationEpoch", disposition.operationEpoch)
        && DispositionIdentity(entry, disposition)
        && Text(entry, "reason", disposition.reason, 64)
        && (PermanentKeepsakeFailure(410, disposition.reason) || PermanentKeepsakeFailure(409, disposition.reason)
            || PermanentKeepsakeFailure(404, disposition.reason) || disposition.reason == "physical_outcome_unknown"
            || PreparationDisposition(disposition.reason) || disposition.reason == "outcome_delivered");
    if (valid) dispositions.push_back(std::move(disposition));
  }
  if (prepared && (!cJSON_IsArray(prepared) || cJSON_GetArraySize(prepared) > static_cast<int>(KeepsakeOutbox::Capacity))) valid = false;
  if (version >= 2 && !prepared) valid = false;
  for (int i = 0; valid && prepared && i < cJSON_GetArraySize(prepared); ++i) {
    auto* entry = cJSON_GetArrayItem(prepared, i);
    KeepsakeOutcome item;
    uint64_t compartment = 0;
    valid = UniqueIdentityFields(entry) && Text(entry, "operationId", item.operationId) && Text(entry, "outcomeId", item.outcomeId)
        && Text(entry, "proposalId", item.proposalId) && Text(entry, "physicalOperationId", item.physicalOperationId)
        && Text(entry, "bootId", item.bootId) && Number(entry, "generation", item.generation)
        && Number(entry, "deviceRevision", item.deviceRevision) && Number(entry, "compartment", compartment)
        && compartment >= 1 && compartment <= 6 && Identity(entry, item) && Valid(item);
    item.compartment = static_cast<uint8_t>(compartment);
    if (valid) preparations.push_back(std::move(item));
  }
  cJSON_Delete(root);
  if (!valid) return false;
  state = {std::move(outcomes), std::move(dispositions), std::move(preparations)};
  return true;
}
}  // namespace keepsake_persistence

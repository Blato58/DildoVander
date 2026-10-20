#include "OfflineShow.h"

#include <cstddef>
#include <cstring>
#include <type_traits>

namespace kufr { namespace offline {
namespace storage {
static_assert(sizeof(LegacyOutcomeV1) == 144 && offsetof(LegacyOutboxV1, records) == 24
    && sizeof(LegacyOutboxV1) == 1176, "Legacy NVS layout must retain its original record stride");
static_assert(std::is_trivially_copyable<Outbox>::value, "NVS outbox requires a POD representation");
bool DecodeOutboxBytes(const void* bytes, size_t length, Outbox& outbox) {
  if (!bytes) return false;
  if (length == sizeof(Outbox)) {
    Outbox decoded;
    std::memcpy(&decoded, bytes, sizeof(decoded));
    if (decoded.version != 2) return false;
    outbox = decoded;
    return true;
  }
  if (length != sizeof(LegacyOutboxV1)) return false;
  LegacyOutboxV1 legacy;
  std::memcpy(&legacy, bytes, sizeof(legacy));
  if (legacy.version != 1) return false;
  Outbox migrated;
  migrated.offlineEnabled = legacy.offlineEnabled;
  migrated.ambientEnabled = legacy.ambientEnabled;
  migrated.quiet = legacy.quiet;
  migrated.serverSettingsRevision = legacy.serverSettingsRevision;
  migrated.serverStopRevision = legacy.serverStopRevision;
  for (size_t index = 0; index < legacy.records.size(); ++index) {
    const auto& source = legacy.records[index];
    auto& target = migrated.records[index];
    target.operationId = source.operationId; target.bootId = source.bootId;
    target.localEveningId = source.localEveningId;
    target.generation = source.generation; target.startedAtMs = source.startedAtMs;
    target.completedAtMs = source.completedAtMs; target.slot = source.slot;
    target.completed = source.completed; target.scripted = source.scripted;
    target.shortVariant = source.shortVariant;
    // Missing identity stays epoch zero; no completion evidence is synthesized.
  }
  outbox = migrated;
  return true;
}
}

}}

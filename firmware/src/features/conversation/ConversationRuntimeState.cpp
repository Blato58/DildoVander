#include "ConversationRuntimeState.h"

namespace {

bool IsValidCatalogVersion(const std::string& value) {
  if (value.empty() || value.size() > 128) {
    return false;
  }
  for (unsigned char character : value) {
    const bool letter = (character >= 'a' && character <= 'z') ||
                        (character >= 'A' && character <= 'Z');
    const bool digit = character >= '0' && character <= '9';
    if (!letter && !digit && character != '.' && character != '_' &&
        character != '-') {
      return false;
    }
  }
  return true;
}

}  // namespace

ConversationRuntimeState::ConversationRuntimeState(
    bool available, uint8_t readyMask, const std::string& catalogVersion) {
  if (available && readyMask <= 63 &&
      IsValidCatalogVersion(catalogVersion)) {
    snapshot_.readyMask = readyMask;
    snapshot_.catalogVersion = catalogVersion;
    snapshot_.version = 1;
    available_ = true;
  }
}

bool ConversationRuntimeState::Read(
    ConversationRuntimeSnapshot& snapshot) const {
  std::lock_guard<std::mutex> lock(mutex_);
  if (!available_) {
    return false;
  }
  snapshot = snapshot_;
  return true;
}

bool ConversationRuntimeState::Matches(
    const ConversationRuntimeSnapshot& snapshot) const {
  std::lock_guard<std::mutex> lock(mutex_);
  return available_ && snapshot.version == snapshot_.version &&
         snapshot.generation == snapshot_.generation &&
         snapshot.deviceRevision == snapshot_.deviceRevision &&
         snapshot.operationEpoch == snapshot_.operationEpoch &&
         snapshot.readyMask == snapshot_.readyMask &&
         snapshot.catalogVersion == snapshot_.catalogVersion;
}

bool ConversationRuntimeState::Publish(
    uint64_t generation, uint64_t deviceRevision, uint8_t readyMask,
    const std::string& catalogVersion, uint64_t operationEpoch) {
  if (readyMask > 63 || !IsValidCatalogVersion(catalogVersion)) {
    return false;
  }
  std::lock_guard<std::mutex> lock(mutex_);
  if (snapshot_.version != 0 &&
      (generation < snapshot_.generation ||
       operationEpoch < snapshot_.operationEpoch ||
       deviceRevision < snapshot_.deviceRevision ||
       (deviceRevision == snapshot_.deviceRevision &&
        (readyMask != snapshot_.readyMask ||
         catalogVersion != snapshot_.catalogVersion)))) {
    return false;
  }
  snapshot_.generation = generation;
  snapshot_.deviceRevision = deviceRevision;
  snapshot_.operationEpoch = operationEpoch;
  snapshot_.readyMask = readyMask;
  snapshot_.catalogVersion = catalogVersion;
  ++snapshot_.version;
  available_ = true;
  return true;
}

void ConversationRuntimeState::Invalidate() {
  std::lock_guard<std::mutex> lock(mutex_);
  ++snapshot_.version;
  available_ = false;
}

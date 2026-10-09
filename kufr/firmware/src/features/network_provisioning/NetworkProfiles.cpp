#include "NetworkProfiles.h"

#include <cstddef>
#include <cstring>

namespace {
bool Valid(const WifiProfile& profile) {
  if (std::memchr(profile.ssid, 0, sizeof(profile.ssid)) == nullptr ||
      std::memchr(profile.password, 0, sizeof(profile.password)) == nullptr) {
    return false;
  }
  const size_t ssidLength = std::strlen(profile.ssid);
  const size_t passwordLength = std::strlen(profile.password);
  if (ssidLength == 0) {
    return passwordLength == 0;
  }
  if (passwordLength < 8 || passwordLength > 63) {
    return false;
  }
  for (size_t i = 0; i < ssidLength; ++i) {
    if (static_cast<unsigned char>(profile.ssid[i]) < 0x20) { return false; }
  }
  for (size_t i = 0; i < passwordLength; ++i) {
    const auto value = static_cast<unsigned char>(profile.password[i]);
    if (value < 0x20 || value > 0x7e) { return false; }
  }
  return true;
}
WifiProfile* Mutable(NetworkProfileRecord& record, NetworkProfile profile) {
  switch (profile) {
    case NetworkProfile::Hotspot: return &record.hotspot;
    case NetworkProfile::Fallback: return &record.fallback;
    default: return nullptr;
  }
}
}

void NetworkProfiles::Wipe(void* data, size_t length) {
  auto* bytes = static_cast<volatile uint8_t*>(data);
  for (size_t i = 0; i < length; ++i) { bytes[i] = 0; }
}
NetworkProfiles::~NetworkProfiles() { Wipe(&record_, sizeof(record_)); }
bool NetworkProfiles::ValidRecord(const NetworkProfileRecord& record) {
  return record.version == 1 && Valid(record.hotspot) && Valid(record.fallback) &&
         static_cast<uint8_t>(record.lastSuccessful) <= 2;
}
bool NetworkProfiles::Load(const NetworkProfileRecord& record) {
  if (!ValidRecord(record)) { return false; }
  record_ = record;
  return true;
}
bool NetworkProfiles::MakeCandidate(const char* ssid, const char* password,
                                    WifiProfile& out) {
  Wipe(&out, sizeof(out));
  if (ssid == nullptr || password == nullptr || std::strlen(ssid) == 0 ||
      std::strlen(ssid) > 32 || std::strlen(password) > 63) { return false; }
  std::strcpy(out.ssid, ssid);
  std::strcpy(out.password, password);
  if (Valid(out)) { return true; }
  Wipe(&out, sizeof(out));
  return false;
}
bool NetworkProfiles::Commit(NetworkProfileRecord& next) {
  const bool saved = storage_.Save(next);
  if (saved) { record_ = next; }
  else {
    // A storage adapter can commit the new blob and then fail legacy cleanup.
    // Restore the prior blob once; persistent storage faults may also reject
    // this rollback, so the operation remains failed and RAM stays unchanged.
    storage_.Save(record_);
  }
  Wipe(&next, sizeof(next));
  return saved;
}
bool NetworkProfiles::CommitReplacement(NetworkProfile profile,
                                        const WifiProfile& candidate) {
  if (!candidate.Configured() || !Valid(candidate)) { return false; }
  auto next = record_;
  auto* target = Mutable(next, profile);
  if (target == nullptr) { Wipe(&next, sizeof(next)); return false; }
  *target = candidate;
  next.lastSuccessful = profile;
  return Commit(next);
}
bool NetworkProfiles::Delete(NetworkProfile profile) {
  auto next = record_;
  auto* target = Mutable(next, profile);
  if (target == nullptr) { Wipe(&next, sizeof(next)); return false; }
  Wipe(target, sizeof(*target));
  if (next.lastSuccessful == profile) { next.lastSuccessful = NetworkProfile::None; }
  return Commit(next);
}
bool NetworkProfiles::MarkSuccessful(NetworkProfile profile) {
  const auto* target = Get(profile);
  if (target == nullptr || !target->Configured()) { return false; }
  if (record_.lastSuccessful == profile) { return true; }
  auto next = record_;
  next.lastSuccessful = profile;
  return Commit(next);
}
const WifiProfile* NetworkProfiles::Get(NetworkProfile profile) const {
  if (profile == NetworkProfile::Hotspot) { return &record_.hotspot; }
  if (profile == NetworkProfile::Fallback) { return &record_.fallback; }
  return nullptr;
}
NetworkProfile NetworkProfiles::First() const {
  return record_.hotspot.Configured() ? NetworkProfile::Hotspot :
      record_.fallback.Configured() ? NetworkProfile::Fallback : NetworkProfile::None;
}
NetworkProfile NetworkProfiles::Other(NetworkProfile profile) const {
  const auto other = profile == NetworkProfile::Hotspot ?
      NetworkProfile::Fallback : NetworkProfile::Hotspot;
  return Get(other)->Configured() ? other : NetworkProfile::None;
}

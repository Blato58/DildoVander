#include <cassert>
#include <cstring>
#include "../src/features/network_provisioning/NetworkProfiles.h"
#include "../src/features/backend_access/NetworkSettings.h"

class Storage final : public NetworkProfileStorage {
 public:
  bool Save(const NetworkProfileRecord& record) override {
    ++writes;
    if (fail) { return false; }
    saved = record;
    return true;
  }
  bool fail = false;
  int writes = 0;
  NetworkProfileRecord saved;
};
int main() {
  Storage store;
  NetworkProfiles profiles(store);
  assert(profiles.First() == NetworkProfile::None);
  WifiProfile hotspot, fallback, replacement;
  assert(NetworkProfiles::MakeCandidate("Owner iPhone", "password123", hotspot));
  assert(NetworkProfiles::MakeCandidate("Event WiFi", "fallback123", fallback));
  assert(NetworkProfiles::MakeCandidate("Replacement", "replacement123", replacement));
  assert(!NetworkProfiles::MakeCandidate("", "password123", replacement));
  assert(!NetworkProfiles::MakeCandidate("Phone", "short", replacement));
  assert(!NetworkProfiles::MakeCandidate("Phone", "newline\npass", replacement));
  assert(profiles.CommitReplacement(NetworkProfile::Fallback, fallback));
  assert(profiles.First() == NetworkProfile::Fallback);
  assert(profiles.CommitReplacement(NetworkProfile::Hotspot, hotspot));
  assert(profiles.First() == NetworkProfile::Hotspot);
  assert(profiles.Other(NetworkProfile::Hotspot) == NetworkProfile::Fallback);
  assert(profiles.Record().lastSuccessful == NetworkProfile::Hotspot);
  assert(NetworkProfiles::MakeCandidate("Replacement", "replacement123", replacement));
  store.fail = true;
  assert(!profiles.CommitReplacement(NetworkProfile::Hotspot, replacement));
  assert(std::strcmp(profiles.Get(NetworkProfile::Hotspot)->ssid, "Owner iPhone") == 0);
  assert(!profiles.Delete(NetworkProfile::Hotspot));
  assert(profiles.First() == NetworkProfile::Hotspot);
  store.fail = false;
  assert(profiles.Delete(NetworkProfile::Hotspot));
  assert(profiles.First() == NetworkProfile::Fallback);
  assert(profiles.Record().lastSuccessful == NetworkProfile::None);
  NetworkProfiles rebooted(store);
  assert(rebooted.Load(store.saved));
  assert(std::strcmp(rebooted.Get(NetworkProfile::Fallback)->password, "fallback123") == 0);
  auto corrupt = store.saved;
  corrupt.version = 2;
  assert(!rebooted.Load(corrupt));
  corrupt = store.saved;
  std::memset(corrupt.hotspot.ssid, 'x', sizeof(corrupt.hotspot.ssid));
  assert(!rebooted.Load(corrupt));
  assert(rebooted.MarkSuccessful(NetworkProfile::Fallback));
  const int writes = store.writes;
  assert(rebooted.MarkSuccessful(NetworkProfile::Fallback));
  assert(store.writes == writes);
  assert(!rebooted.CommitReplacement(NetworkProfile::None, replacement));
  assert(rebooted.Delete(NetworkProfile::Fallback));
  assert(rebooted.First() == NetworkProfile::None);
  NetworkSettings backend;
  assert(backend.ConfigureBackend("backend.example.org"));
  assert(backend.IsConfigured());
  assert(std::strcmp(backend.BackendHost(), "backend.example.org") == 0);
  assert(backend.Ssid()[0] == 0);
  assert(!backend.ConfigureBackend("http://unsafe"));
}

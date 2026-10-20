#include <assert.h>

#include <string>

#include "../lib/device_configuration/include/NetworkSettings.h"
#include "../src/features/backend_access/BuildDate.h"
#include "../src/features/backend_access/ReconnectPolicy.h"
#include "../src/features/backend_access/TrustedTime.h"

namespace {

void TestServiceNetworkSettingsValidation() {
  NetworkSettings settings;
  assert(!settings.IsConfigured());
  assert(settings.Configure("Event Wifi", "long-secret-passphrase",
                            "api.example.com"));
  assert(settings.IsConfigured());
  assert(std::string(settings.Ssid()) == "Event Wifi");
  assert(std::string(settings.BackendHost()) == "api.example.com");

  assert(!settings.Configure("Event Wifi", "short", "api.example.com"));
  assert(!settings.IsConfigured());
  assert(settings.Password() == nullptr);
  assert(!settings.Configure("Event Wifi", "long-secret-passphrase",
                             "api.example.com/evil"));
  assert(!settings.Configure("Event Wifi", "long-secret-passphrase",
                             "api.example.com:444"));
  assert(!settings.Configure("Event Wifi", "long-secret-passphrase",
                             "-bad.example.com"));
  assert(!settings.Configure("Event Wifi", "long-secret-passphrase",
                             "api..example.com"));
  assert(!settings.Configure("Event\nWifi", "long-secret-passphrase",
                             "api.example.com"));
  assert(settings.Configure("Event Wifi", "long-secret-passphrase",
                            "api.example.com"));
  settings.Reset();
  assert(!settings.IsConfigured());
}

void TestReconnectBackoff() {
  ReconnectPolicy policy;
  assert(policy.NextDelayMs(200) == 1000);
  assert(policy.NextDelayMs(200) == 2000);
  assert(policy.NextDelayMs(200) == 4000);
  assert(policy.NextDelayMs(200) == 8000);
  assert(policy.NextDelayMs(200) == 16000);
  assert(policy.NextDelayMs(200) == 30000);
  assert(policy.NextDelayMs(400) == 30000);
  policy.Reset();
  assert(policy.NextDelayMs(0) == 800);
}

class MemoryTimeStore final : public LastTrustedTimeStore {
 public:
  LastTrustedTimeRead Read(uint64_t& epochSeconds) override {
    if (readFailure) {
      return LastTrustedTimeRead::Failed;
    }
    if (!present) {
      return LastTrustedTimeRead::Missing;
    }
    epochSeconds = stored;
    return LastTrustedTimeRead::Found;
  }

  bool Write(uint64_t epochSeconds) override {
    ++writes;
    if (writeFailure) {
      return false;
    }
    stored = epochSeconds;
    present = true;
    return true;
  }

  uint64_t stored = 0;
  int writes = 0;
  bool present = false;
  bool readFailure = false;
  bool writeFailure = false;
};

void TestTimeTrustAcrossRestart() {
  constexpr uint64_t buildTime = 1800000000;
  MemoryTimeStore storage;
  TrustedTime initial(storage, buildTime);
  assert(initial.IsHealthy());
  assert(!initial.CheckAndRecord(buildTime - 3 * 24 * 60 * 60));
  assert(initial.CheckAndRecord(buildTime));
  assert(storage.writes == 1);
  assert(initial.CheckAndRecord(buildTime + 60));
  assert(storage.writes == 1);

  TrustedTime restarted(storage, buildTime);
  assert(!restarted.CheckAndRecord(buildTime - 301));
  assert(restarted.CheckAndRecord(buildTime - 300));
  assert(restarted.CheckAndRecord(buildTime + 24 * 60 * 60));
  assert(storage.writes == 2);

  storage.writeFailure = true;
  assert(!restarted.CheckAndRecord(buildTime + 2 * 24 * 60 * 60));
  assert(!restarted.IsHealthy());
  assert(!restarted.CheckAndRecord(buildTime + 2 * 24 * 60 * 60));
}

void TestBuildDate() {
  assert(ParseBuildDateUtc("Jan  1 1970") == 0);
  assert(ParseBuildDateUtc("Jan  2 1970") == 86400);
  assert(ParseBuildDateUtc("Mar  1 2000") -
             ParseBuildDateUtc("Feb 29 2000") == 86400);
  assert(ParseBuildDateUtc("Feb 29 2001") == 0);
  assert(ParseBuildDateUtc("Sep 29 2026") > 0);
}

}  // namespace

int main() {
  TestServiceNetworkSettingsValidation();
  TestReconnectBackoff();
  TestTimeTrustAcrossRestart();
  TestBuildDate();
  return 0;
}

#pragma once

#include <cstdint>
#include <cstddef>

enum class NetworkProfile : uint8_t { None = 0, Hotspot = 1, Fallback = 2 };

struct WifiProfile {
  char ssid[33]{};
  char password[64]{};
  bool Configured() const { return ssid[0] != 0; }
};

// One versioned blob is the commit unit. A failed candidate never replaces it.
struct NetworkProfileRecord {
  uint32_t version = 1;
  WifiProfile hotspot;
  WifiProfile fallback;
  NetworkProfile lastSuccessful = NetworkProfile::None;
};

class NetworkProfileStorage {
 public:
  virtual ~NetworkProfileStorage() = default;
  virtual bool Save(const NetworkProfileRecord& record) = 0;
};

class NetworkProfiles final {
 public:
  explicit NetworkProfiles(NetworkProfileStorage& storage) : storage_(storage) {}
  ~NetworkProfiles();
  bool Load(const NetworkProfileRecord& record);
  bool CommitReplacement(NetworkProfile profile, const WifiProfile& candidate);
  bool Delete(NetworkProfile profile);
  bool MarkSuccessful(NetworkProfile profile);
  const WifiProfile* Get(NetworkProfile profile) const;
  NetworkProfile First() const;
  NetworkProfile Other(NetworkProfile profile) const;
  const NetworkProfileRecord& Record() const { return record_; }
  static bool MakeCandidate(const char* ssid, const char* password, WifiProfile& out);
  static bool ValidRecord(const NetworkProfileRecord& record);
  static void Wipe(void* data, size_t length);

 private:
  bool Commit(NetworkProfileRecord& next);
  NetworkProfileStorage& storage_;
  NetworkProfileRecord record_;
};

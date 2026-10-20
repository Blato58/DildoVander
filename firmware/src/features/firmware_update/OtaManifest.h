#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace kufr { namespace ota {

constexpr size_t MaximumManifestBytes = 4096;
constexpr uint32_t UpdaterVersion = 1;

struct Manifest final {
  std::string version;
  std::string buildId;
  std::string hardware;
  std::string profile;
  std::string channel;
  std::string url;
  std::string sha256;
  uint32_t sizeBytes = 0;
  uint32_t updaterVersion = 0;
  uint32_t minUpdaterVersion = 0;
};

struct Envelope final {
  std::string payload;
  std::vector<uint8_t> signature;
};

bool ParseEnvelope(const std::string& json, Envelope& envelope);
bool ParseManifest(const std::string& payload, Manifest& manifest);
bool IsApprovedOrigin(const std::string& origin);
bool IsSemanticVersion(const std::string& version);
// Returns -1, 0, 1; both arguments must first pass IsSemanticVersion.
int CompareVersions(const std::string& left, const std::string& right);
// Empty means compatible; "no-update" means equal/older version or same build.
std::string ValidateUpgrade(const Manifest& manifest,
                           const std::string& installedVersion,
                           const std::string& installedBuild,
                           const std::string& channel, uint32_t partitionBytes);

} }  // namespace kufr::ota

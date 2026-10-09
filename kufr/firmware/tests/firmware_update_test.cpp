#include "../src/features/firmware_update/OtaManifest.h"
#include "../src/features/firmware_update/OtaPolicy.h"

#include <cassert>
#include <iostream>

using namespace kufr::ota;
namespace {
std::string Payload() {
  return "{\"schemaVersion\":1,\"version\":\"1.2.3\",\"buildId\":\"" + std::string(40, 'a') +
      "\",\"hardware\":\"esp32s3-n16r8\",\"profile\":\"B-effects\",\"channel\":\"stable\",\"url\":\"/v1/firmware/artifacts/" +
      std::string(64, 'b') + ".bin\",\"sizeBytes\":4096,\"sha256\":\"" + std::string(64, 'b') +
      "\",\"updaterVersion\":1,\"minUpdaterVersion\":1,\"signatureAlgorithm\":\"ecdsa-p256-sha256\"}";
}
std::string Replace(std::string text, const std::string& from, const std::string& to) {
  const auto found = text.find(from);
  assert(found != std::string::npos);
  text.replace(found, from.size(), to);
  return text;
}
std::string Encode(const std::string& bytes) {
  const char* alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
  std::string result;
  for (size_t i = 0; i < bytes.size(); i += 3) {
    unsigned a = static_cast<unsigned char>(bytes[i]);
    unsigned b = i + 1 < bytes.size() ? static_cast<unsigned char>(bytes[i + 1]) : 0;
    unsigned c = i + 2 < bytes.size() ? static_cast<unsigned char>(bytes[i + 2]) : 0;
    result.push_back(alphabet[a >> 2]);
    result.push_back(alphabet[((a & 3) << 4) | (b >> 4)]);
    result.push_back(i + 1 < bytes.size() ? alphabet[((b & 15) << 2) | (c >> 6)] : '=');
    result.push_back(i + 2 < bytes.size() ? alphabet[c & 63] : '=');
  }
  return result;
}
void Parser() {
  Manifest manifest;
  const auto payload = Payload();
  assert(ParseManifest(payload, manifest));
  assert(manifest.sizeBytes == 4096 && manifest.version == "1.2.3");
  assert(!ParseManifest(payload + " {}", manifest));
  assert(!ParseManifest(Replace(payload, "\"schemaVersion\":1", "\"schemaVersion\":2"), manifest));
  assert(!ParseManifest(Replace(payload, "\"version\":\"1.2.3\"", "\"version\":\"1.2.3\",\"version\":\"9.0.0\""), manifest));
  assert(!ParseManifest(Replace(payload, "4096", "4294967296"), manifest));
  assert(!ParseManifest(Replace(payload, "4096", "04096"), manifest));
  assert(!ParseManifest(Replace(payload, "4096", "-4096"), manifest));
  assert(!ParseManifest(Replace(payload, "4096", "4096.0"), manifest));
  assert(!ParseManifest(Replace(payload, "4096", "\"4096\""), manifest));
  assert(!ParseManifest(Replace(payload, "4096", "1023"), manifest));
  assert(!ParseManifest(Replace(payload, "ecdsa-p256-sha256", "none"), manifest));
  assert(!ParseManifest(Replace(payload, "/v1/firmware/artifacts/", "https://evil.test/v1/firmware/artifacts/"), manifest));
  assert(!ParseManifest(Replace(payload, "/v1/firmware/artifacts/", "/v1/firmware/artifacts/../"), manifest));
  assert(!ParseManifest(Replace(payload, ".bin", ".bin?secret=1"), manifest));
  assert(!ParseManifest(Replace(payload, std::string(64, 'b') + ".bin", std::string(64, 'c') + ".bin"), manifest));
  assert(!ParseManifest(Replace(payload, "\"channel\":\"stable\"", "\"channel\":\"stable\",\"unknown\":1"), manifest));
  assert(!ParseManifest(Replace(payload, "1.2.3", "01.2.3"), manifest));
  assert(!ParseManifest(Replace(payload, "1.2.3", "1.2"), manifest));
  assert(!ParseManifest(Replace(payload, "1.2.3", "1.2.3-01"), manifest));
  assert(!ParseManifest(Replace(payload, "1.2.3", "1.2.3\\u0000"), manifest));
  Envelope envelope;
  const std::string json = "{\"schemaVersion\":1,\"payload\":\"" + Encode(payload) + "\",\"signature\":\"" + Encode(std::string(70, 's')) + "\"}";
  assert(ParseEnvelope(json, envelope) && envelope.payload == payload && envelope.signature.size() == 70);
  assert(!ParseEnvelope(Replace(json, "\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"), envelope));
  assert(!ParseEnvelope(Replace(json, Encode(std::string(70, 's')), "!!!!!!!!!!!!"), envelope));
  assert(!ParseEnvelope(Replace(json, Encode(std::string(70, 's')), "YQ=========="), envelope));
  assert(!ParseEnvelope(Replace(json, Encode(std::string(70, 's')), Encode(std::string(90, 's'))), envelope));
  assert(!ParseEnvelope(std::string(MaximumManifestBytes + 1, 'x'), envelope));
  assert(!ParseEnvelope(Replace(json, Encode(payload), Encode(std::string(2050, 'x'))), envelope));
}
void VersionsAndOrigin() {
  assert(IsApprovedOrigin("https://firmware.example.test"));
  assert(IsApprovedOrigin("https://2.28.202.220:443"));
  for (const char* origin : {"http://example.test", "https://example.test/", "https://example.test:80", "https://example.test@evil.test", "https://-bad.test", "https://example.test?x=y", "https://example.test#hash", "https://example..test"}) assert(!IsApprovedOrigin(origin));
  for (const char* version : {"1.2.3", "0.0.0", "1.2.3-alpha.1", "1.2.3+build.7", "1.2.3-alpha.2+git"}) assert(IsSemanticVersion(version));
  assert(CompareVersions("1.10.0", "1.9.9") > 0);
  assert(CompareVersions("1.0.0", "1.0.0-rc.9") > 0);
  assert(CompareVersions("1.0.0-alpha.10", "1.0.0-alpha.9") > 0);
  assert(CompareVersions("1.0.0-alpha.1", "1.0.0-alpha.beta") < 0);
  assert(CompareVersions("1.0.0+one", "1.0.0+two") == 0);
  Manifest manifest;
  assert(ParseManifest(Payload(), manifest));
  assert(ValidateUpgrade(manifest, "1.2.2", std::string(40, 'c'), "stable", 4096).empty());
  assert(ValidateUpgrade(manifest, "1.2.3", "old", "stable", 4096) == "no-update");
  assert(ValidateUpgrade(manifest, "2.0.0", "old", "stable", 4096) == "no-update");
  assert(ValidateUpgrade(manifest, "1.0.0", manifest.buildId, "stable", 4096) == "no-update");
  assert(ValidateUpgrade(manifest, "1.0.0", "old", "stable", 4095) == "image-too-large");
  assert(ValidateUpgrade(manifest, "1.0.0", "old", "development", 4096) == "incompatible-channel");
  manifest.hardware = "esp32";
  assert(ValidateUpgrade(manifest, "1.0.0", "old", "stable", 4096) == "incompatible-hardware");
  manifest.hardware = "esp32s3-n16r8";
  manifest.profile = "A";
  assert(ValidateUpgrade(manifest, "1.0.0", "old", "stable", 4096) == "incompatible-hardware");
  manifest.profile = "B-effects";
  manifest.minUpdaterVersion = 2;
  assert(ValidateUpgrade(manifest, "1.0.0", "old", "stable", 4096) == "updater-too-old");
}
void SchedulingAndHealth() {
  OtaPolicy policy;
  policy.Start(0, false);
  assert(!policy.CheckDue(29999, true, true));
  assert(policy.CheckDue(30000, true, true));
  assert(!policy.CheckDue(30000, false, true));
  assert(!policy.CheckDue(30000, true, false));
  policy.Checked(30000, false);
  assert(policy.NextCheck() == 60000);
  policy.Checked(60000, false);
  assert(policy.NextCheck() == 120000);
  for (unsigned i = 0; i < 12; ++i) policy.Checked(0, false);
  assert(policy.NextCheck() == 960000);
  policy.Checked(500, true);
  assert(policy.NextCheck() == 500 + OtaPolicy::CheckIntervalMs);
  assert(!policy.SetCheckIntervalSeconds(59));
  assert(!policy.SetCheckIntervalSeconds(86401));
  assert(policy.SetCheckIntervalSeconds(60));
  policy.Checked(1000, true);
  assert(policy.NextCheck() == 61000);
  policy.Start(1000, true);
  assert(!policy.CheckDue(90000, true, true));
  assert(policy.BootHealth(1000, true) == BootDecision::Wait);
  assert(policy.BootHealth(5999, true) == BootDecision::Wait);
  assert(policy.BootHealth(6000, true) == BootDecision::Confirm);
  assert(policy.BootHealth(122000, true) == BootDecision::Wait);
  policy.Start(0, true);
  assert(policy.BootHealth(0, true) == BootDecision::Wait);
  assert(policy.BootHealth(4000, false) == BootDecision::Wait);
  assert(policy.BootHealth(5000, true) == BootDecision::Wait);
  assert(policy.BootHealth(9999, true) == BootDecision::Wait);
  assert(policy.BootHealth(10000, true) == BootDecision::Confirm);
  policy.Start(0, true);
  assert(policy.BootHealth(119999, false) == BootDecision::Wait);
  assert(policy.BootHealth(120000, false) == BootDecision::Rollback);
  policy.Start(0, true);
  assert(policy.BootHealth(116000, true) == BootDecision::Wait);
  assert(policy.BootHealth(121000, true) == BootDecision::Rollback);
}
}  // namespace
int main() {
  Parser();
  VersionsAndOrigin();
  SchedulingAndHealth();
  std::cout << "Firmware OTA manifest/security, compatibility, scheduling and rollback policy passed.\n";
}

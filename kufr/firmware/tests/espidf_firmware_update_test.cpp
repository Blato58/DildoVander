#include "ota_stubs/OtaPlatform.h"
#include "../src/features/firmware_update/OtaManifest.h"
#include "../src/features/firmware_update/OtaPolicy.h"
#include "../src/features/firmware_update/EspIdfFirmwareUpdate.h"
#include <cassert>
#include <iostream>

namespace ota_fake {
uint64_t nowMs;
std::map<std::string, std::string> settings;
std::map<std::string, FakeHttp> responses;
std::vector<std::string> requested;
std::vector<uint8_t> staged;
esp_partition_t running{0x10000, 4 * 1024 * 1024}, target{0x410000, 4 * 1024 * 1024};
esp_ota_img_states_t runningState, targetState;
esp_app_desc_t runningDescription, stagedDescription;
bool signatureValid, imageValid, bootPromoted, rebooted, rolledBack, aborted;
unsigned writes;
void Reset() {
  nowMs = 0;
  settings = {{"origin", "https://firmware.example.test"}, {"trust_pem", "PUBLIC TEST KEY"}, {"channel", "stable"}};
  responses.clear(); requested.clear(); staged.clear();
  runningState = ESP_OTA_IMG_VALID; targetState = ESP_OTA_IMG_UNDEFINED;
  runningDescription = {}; stagedDescription = {};
  std::memcpy(runningDescription.version, "0.1.0", 6);
  std::memcpy(stagedDescription.version, "0.2.0", 6);
  std::memcpy(runningDescription.project_name, "kufr", 5);
  std::memcpy(stagedDescription.project_name, "kufr", 5);
  signatureValid = imageValid = true;
  bootPromoted = rebooted = rolledBack = aborted = false;
  writes = 0;
}
}

using namespace kufr::ota;
namespace kufr { namespace ota {
struct FirmwareUpdateTestAccess {
  static void Run(EspIdfFirmwareUpdate& ota) {
    const auto work = ota.work_;
    if (work == EspIdfFirmwareUpdate::Work::Check) ota.Check();
    if (work == EspIdfFirmwareUpdate::Work::Install) ota.Install();
    ota.work_ = EspIdfFirmwareUpdate::Work::None;
  }
};
} }
namespace {
const std::string Origin = "https://firmware.example.test";
const std::string ManifestUrl = Origin + "/v1/firmware/manifests/stable.json";
const std::string ArtifactUrl = Origin + "/v1/firmware/artifacts/" + std::string(64, '0') + ".bin";
std::string Encode(const std::string& bytes) {
  const char* alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
  std::string encoded;
  for (size_t i = 0; i < bytes.size(); i += 3) {
    const unsigned a = static_cast<uint8_t>(bytes[i]);
    const unsigned b = i + 1 < bytes.size() ? static_cast<uint8_t>(bytes[i + 1]) : 0;
    const unsigned c = i + 2 < bytes.size() ? static_cast<uint8_t>(bytes[i + 2]) : 0;
    encoded.push_back(alphabet[a >> 2]); encoded.push_back(alphabet[((a & 3) << 4) | (b >> 4)]);
    encoded.push_back(i + 1 < bytes.size() ? alphabet[((b & 15) << 2) | (c >> 6)] : '=');
    encoded.push_back(i + 2 < bytes.size() ? alphabet[c & 63] : '=');
  }
  return encoded;
}
void Release() {
  const std::string payload = "{\"schemaVersion\":1,\"version\":\"0.2.0\",\"buildId\":\"" + std::string(40, 'a') +
      "\",\"hardware\":\"esp32s3-n16r8\",\"profile\":\"B-effects\",\"channel\":\"stable\",\"url\":\"/v1/firmware/artifacts/" +
      std::string(64, '0') + ".bin\",\"sizeBytes\":8192,\"sha256\":\"" + std::string(64, '0') +
      "\",\"updaterVersion\":1,\"minUpdaterVersion\":1,\"signatureAlgorithm\":\"ecdsa-p256-sha256\"}";
  const std::string envelope = "{\"schemaVersion\":1,\"payload\":\"" + Encode(payload) + "\",\"signature\":\"" + Encode(std::string(70, 's')) + "\"}";
  ota_fake::responses[ManifestUrl] = {envelope, 200, static_cast<int64_t>(envelope.size()), 0, false, false};
  ota_fake::responses[ArtifactUrl] = {std::string(8192, '\0'), 200, 8192, 0, false, false};
}
void Run(EspIdfFirmwareUpdate& ota) {
  FirmwareUpdateTestAccess::Run(ota);
}
void Check(EspIdfFirmwareUpdate& ota) {
  assert(ota.RequestCheck()); ota.Tick(0, true, true, true); Run(ota);
}
void StartInstall(EspIdfFirmwareUpdate& ota) {
  assert(ota.RequestInstall());
  ota.Tick(0, true, true, false);
  assert(ota.Status().state == "waiting-idle" && !ota.Reserved() && ota_fake::writes == 0);
  ota.Tick(0, true, true, true);
  assert(ota.Reserved());
  Run(ota);
}
void Success() {
  ota_fake::Reset(); Release();
  EspIdfFirmwareUpdate ota; assert(ota.Begin());
  assert(!ota.SetCheckIntervalSeconds(59));
  assert(ota.SetCheckIntervalSeconds(60));
  assert(ota_fake::settings["check_interval"] == "60");
  Check(ota); assert(ota.Status().state == "available");
  StartInstall(ota);
  assert(ota_fake::bootPromoted && ota.Status().state == "ready-to-reboot" && ota.Reserved());
  assert(ota_fake::writes == 2 && ota_fake::staged.size() == 8192);
  ota.Tick(100, true, true, false); assert(!ota_fake::rebooted);
  ota.Tick(200, true, true, true); assert(ota_fake::rebooted);
  assert(ota_fake::settings["trust_pem"] == "PUBLIC TEST KEY");
}
void Failures() {
  ota_fake::Reset(); Release();
  ota_fake::signatureValid = false;
  EspIdfFirmwareUpdate signature; assert(signature.Begin()); Check(signature);
  assert(signature.Status().error == "manifest-signature-invalid" && ota_fake::writes == 0 && !signature.RequestInstall());
  ota_fake::Reset(); Release();
  ota_fake::responses[ManifestUrl].status = 302;
  EspIdfFirmwareUpdate redirect; assert(redirect.Begin()); Check(redirect);
  assert(redirect.Status().error == "manifest-http-failed" && ota_fake::requested.size() == 1);
  ota_fake::Reset(); Release();
  EspIdfFirmwareUpdate artifactRedirect; assert(artifactRedirect.Begin()); Check(artifactRedirect);
  ota_fake::responses[ArtifactUrl].status = 307;
  StartInstall(artifactRedirect);
  assert(artifactRedirect.Status().error == "artifact-http-failed" && !ota_fake::bootPromoted && ota_fake::writes == 0);
  ota_fake::Reset(); Release();
  EspIdfFirmwareUpdate corrupt; assert(corrupt.Begin()); Check(corrupt);
  ota_fake::responses[ArtifactUrl].body[0] = 1;
  StartInstall(corrupt);
  assert(corrupt.Status().error == "artifact-sha256-invalid" && !ota_fake::bootPromoted && !corrupt.Reserved());
  ota_fake::Reset(); Release();
  EspIdfFirmwareUpdate interrupted; assert(interrupted.Begin()); Check(interrupted);
  ota_fake::responses[ArtifactUrl].interrupted = true;
  StartInstall(interrupted);
  assert(interrupted.Status().error == "artifact-download-incomplete" && ota_fake::aborted && !ota_fake::bootPromoted);
  ota_fake::Reset(); Release();
  EspIdfFirmwareUpdate wrongLength; assert(wrongLength.Begin()); Check(wrongLength);
  ota_fake::responses[ArtifactUrl].length = 8193;
  StartInstall(wrongLength);
  assert(wrongLength.Status().error == "artifact-http-failed" && ota_fake::writes == 0);
  ota_fake::Reset(); Release();
  EspIdfFirmwareUpdate wrongDescriptor; assert(wrongDescriptor.Begin()); Check(wrongDescriptor);
  std::memcpy(ota_fake::stagedDescription.version, "9.0.0", 6);
  StartInstall(wrongDescriptor);
  assert(wrongDescriptor.Status().error == "artifact-description-mismatch" && !ota_fake::bootPromoted);
  ota_fake::Reset(); Release();
  ota_fake::targetState = ESP_OTA_IMG_INVALID;
  EspIdfFirmwareUpdate repeated; assert(repeated.Begin()); Check(repeated);
  assert(repeated.Status().error == "previously-failed-version" && ota_fake::writes == 0);
}
void BootHealth() {
  ota_fake::Reset();
  ota_fake::settings.clear();
  EspIdfFirmwareUpdate disabled; assert(disabled.Begin());
  assert(disabled.Status().state == "disabled" && !disabled.RequestCheck());
  ota_fake::Reset();
  ota_fake::runningState = ESP_OTA_IMG_PENDING_VERIFY;
  EspIdfFirmwareUpdate health; assert(health.Begin() && health.Reserved());
  assert(!health.RequestCheck());
  health.Tick(0, true, true, true); health.Tick(5000, true, true, true);
  assert(!health.Reserved() && ota_fake::runningState == ESP_OTA_IMG_VALID);
  ota_fake::Reset();
  ota_fake::runningState = ESP_OTA_IMG_PENDING_VERIFY;
  EspIdfFirmwareUpdate unhealthy; assert(unhealthy.Begin());
  unhealthy.Tick(120000, false, false, true);
  assert(ota_fake::rolledBack && unhealthy.Reserved());
}
}
int main() {
  Success(); Failures(); BootHealth();
  std::cout << "ESP-IDF OTA adapter success/failure, safe reservation and boot recovery tests passed (fake HTTP/flash/crypto).\n";
}

#include "ota_stubs/OtaPlatform.h"
#include "../src/features/firmware_update/OtaManifest.h"
#include "../src/features/firmware_update/OtaPolicy.h"
#include "../src/features/firmware_update/EspIdfFirmwareUpdate.h"
#include "../src/features/physical_effects/PhysicalEffects.h"
#include "../src/features/device_control/PersistentCommandJournal.h"
#include <cassert>
#include <iostream>
#include <new>

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
unsigned begins;
size_t partitionBytesRead;
const void* downloadBuffer;
bool reusedTransferBuffer, failTransferAllocation;
unsigned transferAllocations;
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
  begins = 0;
  partitionBytesRead = 0;
  downloadBuffer = nullptr;
  reusedTransferBuffer = true;
  failTransferAllocation = false;
  transferAllocations = 0;
}
}

// Fault only the updater's nothrow 4 KiB array, without a production test hook.
void* operator new[](size_t bytes, const std::nothrow_t&) noexcept {
  if (bytes == 4096) {
    ++ota_fake::transferAllocations;
    if (ota_fake::failTransferAllocation) return nullptr;
  }
  try { return ::operator new[](bytes); }
  catch (...) { return nullptr; }
}
void operator delete[](void* bytes, const std::nothrow_t&) noexcept { ::operator delete[](bytes); }

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
void Release(size_t imageSize = 8192) {
  const std::string payload = "{\"schemaVersion\":1,\"version\":\"0.2.0\",\"buildId\":\"" + std::string(40, 'a') +
      "\",\"hardware\":\"esp32s3-n16r8\",\"profile\":\"B-effects\",\"channel\":\"stable\",\"url\":\"/v1/firmware/artifacts/" +
      std::string(64, '0') + ".bin\",\"sizeBytes\":" + std::to_string(imageSize) + ",\"sha256\":\"" + std::string(64, '0') +
      "\",\"updaterVersion\":1,\"minUpdaterVersion\":1,\"signatureAlgorithm\":\"ecdsa-p256-sha256\"}";
  const std::string envelope = "{\"schemaVersion\":1,\"payload\":\"" + Encode(payload) + "\",\"signature\":\"" + Encode(std::string(70, 's')) + "\"}";
  ota_fake::responses[ManifestUrl] = {envelope, 200, static_cast<int64_t>(envelope.size()), 0, false, false};
  ota_fake::responses[ArtifactUrl] = {std::string(imageSize, '\0'), 200, static_cast<int64_t>(imageSize), 0, false, false};
}
void Run(EspIdfFirmwareUpdate& ota) {
  FirmwareUpdateTestAccess::Run(ota);
}
void Check(EspIdfFirmwareUpdate& ota) {
  assert(ota.RequestCheck()); ota.Tick(0, true, true, true); Run(ota);
}
void StartInstall(EspIdfFirmwareUpdate& ota) {
  assert(ota.RequestInstall());
  ota.Tick(0, false, true, true);
  assert(ota.Status().state == "waiting-idle" && !ota.Reserved() && ota_fake::writes == 0);
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
  assert(ota_fake::transferAllocations == 1 && ota_fake::begins == 1);
  assert(ota_fake::partitionBytesRead == 8192 && ota_fake::reusedTransferBuffer);
  ota.Tick(100, true, true, false); assert(!ota_fake::rebooted);
  ota.Tick(200, true, true, true); assert(ota_fake::rebooted);
  assert(ota_fake::settings["trust_pem"] == "PUBLIC TEST KEY");
}
void TransferBufferAndExactLength() {
  ota_fake::Reset(); Release();
  EspIdfFirmwareUpdate unavailable; assert(unavailable.Begin()); Check(unavailable);
  ota_fake::failTransferAllocation = true;
  StartInstall(unavailable);
  assert(unavailable.Status().error == "update-buffer-unavailable" && !unavailable.Reserved());
  assert(ota_fake::transferAllocations == 1 && ota_fake::begins == 0 && ota_fake::writes == 0);
  assert(!ota_fake::bootPromoted && !ota_fake::rebooted && ota_fake::requested.size() == 1);
  assert(ota_fake::runningState == ESP_OTA_IMG_VALID);

  // The final partial block must be written and verified by the same buffer.
  ota_fake::Reset(); Release(8205);
  EspIdfFirmwareUpdate partial; assert(partial.Begin()); Check(partial); StartInstall(partial);
  assert(ota_fake::bootPromoted && ota_fake::writes == 3 && ota_fake::staged.size() == 8205);
  assert(ota_fake::partitionBytesRead == 8205 && ota_fake::reusedTransferBuffer);
  assert(ota_fake::transferAllocations == 1);

  ota_fake::Reset(); Release(8205);
  EspIdfFirmwareUpdate corruptTail; assert(corruptTail.Begin()); Check(corruptTail);
  ota_fake::responses[ArtifactUrl].body.back() = 1;
  StartInstall(corruptTail);
  assert(corruptTail.Status().error == "artifact-sha256-invalid" && !ota_fake::bootPromoted && !corruptTail.Reserved());
  assert(ota_fake::partitionBytesRead == 8205 && ota_fake::reusedTransferBuffer);

  ota_fake::Reset(); Release(8205);
  EspIdfFirmwareUpdate truncated; assert(truncated.Begin()); Check(truncated);
  ota_fake::responses[ArtifactUrl].body.pop_back();
  StartInstall(truncated);
  assert(truncated.Status().error == "artifact-download-incomplete" && ota_fake::aborted && !truncated.Reserved());
  assert(!ota_fake::bootPromoted && ota_fake::partitionBytesRead == 0);
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
class BootPhysicalIo final : public PhysicalIo {
 public:
  bool enabled = false;
  bool failRead = false, failOff = false;
  unsigned enableCount = 0, servoWrites = 0, vibrationWrites = 0;
  void EnableOutputs(bool value) override { enabled = value; if (value) ++enableCount; }
  bool FullOff() override { assert(!enabled); return !failOff; }
  bool Servo(uint8_t, uint16_t) override { ++servoWrites; return true; }
  bool Vibration(uint8_t, bool) override { ++vibrationWrites; return true; }
  bool Lights(const std::array<uint8_t, 108>&) override { return true; }
  bool ReadInputs(PhysicalInputs& inputs) override { inputs = {}; return !failRead; }
};

class BootJournalStorage final : public JournalStorage {
 public:
  std::vector<uint8_t> bytes;
  bool failRead = false;
  unsigned writes = 0;
  JournalReadResult Read(uint8_t* output, size_t capacity, size_t& length) override {
    if (failRead || bytes.size() > capacity) return JournalReadResult::Failed;
    if (bytes.empty()) return JournalReadResult::Missing;
    std::copy(bytes.begin(), bytes.end(), output);
    length = bytes.size();
    return JournalReadResult::Found;
  }
  bool Write(const uint8_t* input, size_t length) override {
    ++writes;
    bytes.assign(input, input + length);
    return true;
  }
};

struct PassiveBoot final {
  BootPhysicalIo io;
  PhysicalEffects effects{io};
  EspIdfFirmwareUpdate ota;
  explicit PassiveBoot(bool failOff = false) {
    ota_fake::Reset();
    ota_fake::runningState = ESP_OTA_IMG_PENDING_VERIFY;
    assert(ota.Begin());
    std::array<ServoCalibration, 6> calibration;
    calibration.fill({1100, 1900, 500});
    io.failOff = failOff;
    assert(effects.Configure(calibration, 1));
  }
  void Tick(uint64_t nowMs, bool runtimeReady = true, bool calibrated = true) {
    effects.Tick(nowMs);
    const bool selfTestReady = BootSelfTestReady(runtimeReady, true, calibrated, effects);
    ota.Tick(nowMs, false, selfTestReady, false);
    assert(!io.enabled && io.enableCount == 0 && io.servoWrites == 0 && io.vibrationWrites == 0);
    assert(!effects.Healthy() && !effects.Move(1, true) && !effects.StartVibration(1));
  }
  void Advance(uint64_t first, uint64_t last, bool runtimeReady = true, bool calibrated = true) {
    for (uint64_t nowMs = first; nowMs <= last; nowMs += 100) Tick(nowMs, runtimeReady, calibrated);
  }
};

void UnattendedBootComposition() {
  BootJournalStorage storage;
  PersistentCommandJournal beforeReboot(storage);
  assert(beforeReboot.PersistReadyMask(63));
  assert(beforeReboot.Begin("523ef590-ce4d-4901-91b3-79de1bfe7e35", 3));
  PersistentCommandJournal recovered(storage);
  assert(recovered.IsHealthy() && recovered.HasPending());
  const auto pendingBytes = storage.bytes;
  const unsigned writes = storage.writes;
  PassiveBoot boot;
  boot.Advance(0, 4900, recovered.IsHealthy());
  assert(boot.ota.Reserved() && ota_fake::runningState == ESP_OTA_IMG_PENDING_VERIFY);
  boot.Tick(5000, recovered.IsHealthy());
  assert(ota_fake::runningState == ESP_OTA_IMG_VALID && !boot.ota.Reserved());
  assert(boot.effects.PassiveHealthy() && !boot.effects.IsResting(1));
  assert(recovered.HasPending() && recovered.PendingCompartment() == 3);
  assert(storage.bytes == pendingBytes && storage.writes == writes);
  assert(ota_fake::requested.empty() && !ota_fake::rolledBack);
}

void PassiveBootFailuresAndReset() {
  PassiveBoot outputFailure(true);
  outputFailure.io.failOff = false;
  outputFailure.Advance(0, 120000);
  assert(!outputFailure.effects.PassiveHealthy() && ota_fake::rolledBack && outputFailure.ota.Reserved());

  PassiveBoot inputFailure;
  inputFailure.Advance(0, 4800);
  inputFailure.io.failRead = true;
  inputFailure.Tick(4900);
  inputFailure.io.failRead = false;
  inputFailure.Advance(5000, 120000);
  assert(!inputFailure.effects.PassiveHealthy() && ota_fake::rolledBack && inputFailure.ota.Reserved());

  BootJournalStorage unavailable;
  unavailable.failRead = true;
  PersistentCommandJournal brokenJournal(unavailable);
  PassiveBoot storageFailure;
  storageFailure.Advance(0, 120000, brokenJournal.IsHealthy());
  assert(storageFailure.effects.PassiveHealthy() && ota_fake::rolledBack && storageFailure.ota.Reserved());

  PassiveBoot missingCalibration;
  missingCalibration.Advance(0, 120000, true, false);
  assert(ota_fake::rolledBack && missingCalibration.ota.Reserved());

  PassiveBoot runtimeRecovery;
  runtimeRecovery.Advance(0, 4800);
  runtimeRecovery.Tick(4900, false);
  runtimeRecovery.Advance(5000, 9900);
  assert(ota_fake::runningState == ESP_OTA_IMG_PENDING_VERIFY && runtimeRecovery.ota.Reserved());
  runtimeRecovery.Tick(10000);
  assert(ota_fake::runningState == ESP_OTA_IMG_VALID && !runtimeRecovery.ota.Reserved());

  PassiveBoot firstBoot;
  firstBoot.Advance(0, 4900);
  PassiveBoot afterReset;
  afterReset.Advance(0, 4900);
  assert(ota_fake::runningState == ESP_OTA_IMG_PENDING_VERIFY && afterReset.ota.Reserved());
  afterReset.Tick(5000);
  assert(ota_fake::runningState == ESP_OTA_IMG_VALID && !afterReset.ota.Reserved());
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
  health.Tick(0, false, true, true); health.Tick(5000, false, true, true);
  assert(!health.Reserved() && ota_fake::runningState == ESP_OTA_IMG_VALID);
  assert(health.RequestCheck());
  health.Tick(6000, false, true, true);
  assert(health.Status().state == "idle" && ota_fake::requested.empty());
  health.Tick(6100, true, true, false);
  assert(health.Status().state == "idle" && ota_fake::requested.empty());
  health.Tick(6200, true, true, true);
  assert(health.Status().state == "checking");
  ota_fake::Reset();
  ota_fake::runningState = ESP_OTA_IMG_PENDING_VERIFY;
  EspIdfFirmwareUpdate unhealthy; assert(unhealthy.Begin());
  unhealthy.Tick(120000, false, false, true);
  assert(ota_fake::rolledBack && unhealthy.Reserved());
}
}
int main() {
  Success(); TransferBufferAndExactLength(); Failures(); UnattendedBootComposition(); PassiveBootFailuresAndReset(); BootHealth();
  std::cout << "ESP-IDF OTA adapter success/failure, safe reservation and boot recovery tests passed (fake HTTP/flash/crypto).\n";
}

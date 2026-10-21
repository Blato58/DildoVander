#include "ota_stubs/OtaPlatform.h"
#include "../src/features/firmware_update/OtaManifest.h"
#include "../src/features/firmware_update/OtaPolicy.h"
#include "../src/features/firmware_update/EspIdfFirmwareUpdate.h"
#include "../src/features/physical_effects/PhysicalEffects.h"
#include "../src/features/device_control/PersistentCommandJournal.h"
#include <cassert>
#include <iostream>
#include <new>
#include <future>
#include <thread>
#include <chrono>

namespace ota_fake {
std::atomic<uint64_t> nowMs;
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
bool failMutex, failTask, confirmSucceeds, commitSucceeds;
unsigned taskCreates, confirms, rollbacks, commits;
thread_local bool onWorker = false;
std::function<void()> confirmHook, commitHook, rollbackHook, restartHook;
std::map<std::string, std::string> pendingSettings;
std::map<std::string, std::vector<uint8_t>> blobs, pendingBlobs;
std::vector<std::string> pendingErases;
bool guardReadFails, guardSecondReadFails, guardWriteFails, guardEraseFails;
bool guardCommitFails, guardEraseCommitFails, guardWriteLostAck, guardEraseLostAck;
unsigned guardReads, guardWrites, guardErases;
std::function<void()> guardReadHook, guardPrepareHook, guardClearHook;
std::vector<std::unique_ptr<std::mutex>> mutexes;
std::mutex taskMutex;
std::condition_variable taskChanged;
void (*taskEntry)(void*) = nullptr;
void* taskContext = nullptr;
unsigned notifications;
bool stopTask;
void Reset() {
  nowMs = 0;
  failMutex = failTask = false;
  confirmSucceeds = commitSucceeds = true;
  taskCreates = confirms = rollbacks = commits = notifications = 0;
  stopTask = false;
  taskEntry = nullptr; taskContext = nullptr;
  confirmHook = commitHook = rollbackHook = restartHook = {};
  pendingSettings.clear(); pendingBlobs.clear(); pendingErases.clear(); blobs.clear();
  guardReadFails = guardSecondReadFails = guardWriteFails = guardEraseFails = false;
  guardCommitFails = guardEraseCommitFails = guardWriteLostAck = guardEraseLostAck = false;
  guardReads = guardWrites = guardErases = 0;
  guardReadHook = guardPrepareHook = guardClearHook = {};
  running = {0x10000, 4 * 1024 * 1024}; target = {0x410000, 4 * 1024 * 1024};
  settings = {{"origin", "https://firmware.example.test"}, {"trust_pem", "PUBLIC TEST KEY"}, {"channel", "stable"}};
  responses.clear(); requested.clear(); staged.clear();
  runningState = ESP_OTA_IMG_VALID; targetState = ESP_OTA_IMG_UNDEFINED;
  runningDescription = {}; stagedDescription = {};
  for (unsigned n = 0; n < 32; ++n) runningDescription.app_elf_sha256[n] = static_cast<uint8_t>(n + 1);
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
    ota_fake::onWorker = true;
    ota.ProcessWork();
    ota_fake::onWorker = false;
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
  assert(!ota.SetCheckIntervalSeconds(60) && ota.LastOperationIsPending());
  assert(ota_fake::settings.count("check_interval") == 0);
  Run(ota);
  assert(ota.SetCheckIntervalSeconds(60) && !ota.LastOperationIsPending());
  assert(ota_fake::settings["check_interval"] == "60");
  Check(ota); assert(ota.Status().state == "available");
  StartInstall(ota);
  assert(ota_fake::bootPromoted && ota.Status().state == "ready-to-reboot" && ota.Reserved());
  assert(ota_fake::writes == 2 && ota_fake::staged.size() == 8192);
  assert(ota_fake::transferAllocations == 1 && ota_fake::begins == 1);
  assert(ota_fake::partitionBytesRead == 8192 && ota_fake::reusedTransferBuffer);
  ota.Tick(100, true, true, false); assert(!ota_fake::rebooted);
  ota.Tick(200, true, true, true); assert(!ota_fake::rebooted); Run(ota); assert(ota_fake::rebooted);
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
    ota_fake::nowMs = nowMs;
    ota.Tick(nowMs, false, selfTestReady, false);
    Run(ota);
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

// Only the fake kernel clock is advanced by tests. These checks establish lock
// ordering and actual adapter dispatch, not ESP scheduler or flash latency.
class ActualWorker final {
 public:
  ActualWorker() : thread_([]() {
    ota_fake::onWorker = true;
    try { ota_fake::taskEntry(ota_fake::taskContext); }
    catch (const ota_fake::StopWorker&) {}
    ota_fake::onWorker = false;
  }) {}
  ~ActualWorker() {
    { std::lock_guard<std::mutex> lock(ota_fake::taskMutex); ota_fake::stopTask = true; }
    ota_fake::taskChanged.notify_all();
    thread_.join();
  }
 private:
  std::thread thread_;
};
void WaitUntil(const std::function<bool()>& ready) {
  const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(3);
  while (!ready() && std::chrono::steady_clock::now() < deadline) std::this_thread::yield();
  assert(ready());
}
void AssertResponsive(EspIdfFirmwareUpdate& ota, uint64_t nowMs, bool reserved) {
  auto control = std::async(std::launch::async, [&]() {
    for (unsigned n = 0; n < 100; ++n) {
      ota.Tick(nowMs, false, true, true);
      assert(ota.Reserved() == reserved);
      (void)ota.Status();
    }
  });
  assert(control.wait_for(std::chrono::seconds(3)) == std::future_status::ready);
  control.get();
}
void DeferredBootActions() {
  ota_fake::Reset();
  ota_fake::settings.clear(); // Pending boot must work without WAN credentials.
  ota_fake::runningState = ESP_OTA_IMG_PENDING_VERIFY;
  EspIdfFirmwareUpdate offline; assert(offline.Begin());
  assert(ota_fake::taskCreates == 1 && offline.Reserved());
  std::promise<void> entered, release;
  auto enteredFuture = entered.get_future(); auto releaseFuture = release.get_future();
  ota_fake::confirmHook = [&]() { entered.set_value(); releaseFuture.wait(); };
  ActualWorker worker;
  offline.Tick(0, false, true, true);
  offline.Tick(4999, false, true, true);
  assert(ota_fake::confirms == 0 && offline.Reserved());
  ota_fake::nowMs = 5000;
  offline.Tick(5000, false, true, true);
  assert(enteredFuture.wait_for(std::chrono::seconds(3)) == std::future_status::ready);
  assert(offline.Status().state == "verifying-boot" && offline.Reserved());
  assert(ota_fake::runningState == ESP_OTA_IMG_PENDING_VERIFY);
  AssertResponsive(offline, 5001, true);
  release.set_value();
  WaitUntil([&]() { return !offline.Reserved(); });
  assert(offline.Status().state == "disabled" && offline.Status().error.empty());
  assert(ota_fake::runningState == ESP_OTA_IMG_VALID && ota_fake::requested.empty());
}
void OriginalBootDeadlineAndRollbackFailure() {
  ota_fake::Reset();
  ota_fake::nowMs = 7000;
  ota_fake::runningState = ESP_OTA_IMG_PENDING_VERIFY;
  EspIdfFirmwareUpdate ota; assert(ota.Begin());
  std::promise<void> entered, release;
  auto enteredFuture = entered.get_future(); auto releaseFuture = release.get_future();
  ota_fake::confirmHook = [&]() { entered.set_value(); releaseFuture.wait(); };
  ActualWorker worker;
  ota.Tick(7000, false, true, true); ota.Tick(12000, false, true, true);
  assert(enteredFuture.wait_for(std::chrono::seconds(3)) == std::future_status::ready);
  ota_fake::nowMs = 126999;
  AssertResponsive(ota, 126999, true);
  assert(ota_fake::rollbacks == 0);
  ota_fake::nowMs = 127000;
  AssertResponsive(ota, 127000, true);
  release.set_value();
  WaitUntil([&]() { return ota.Status().error == "rollback-unavailable"; });
  assert(ota.Reserved() && ota_fake::confirms == 1 && ota_fake::rollbacks == 1);
  // The host rollback API returns (no slot/reboot), and must not be retried.
  ota.Tick(900000, false, true, true);
  assert(ota.Reserved() && ota.Status().state == "rollback");
}
void ConfirmationFailureAndWorkerAllocationFailure() {
  ota_fake::Reset();
  ota_fake::runningState = ESP_OTA_IMG_PENDING_VERIFY;
  ota_fake::confirmSucceeds = false;
  EspIdfFirmwareUpdate failed; assert(failed.Begin());
  failed.Tick(0, false, true, true); failed.Tick(5000, false, true, true);
  Run(failed);
  assert(failed.Reserved() && failed.Status().error == "boot-confirmation-failed");
  assert(ota_fake::confirms == 1 && ota_fake::rollbacks == 0);
  Run(failed);
  assert(failed.Reserved() && failed.Status().error == "rollback-unavailable");
  assert(ota_fake::runningState == ESP_OTA_IMG_PENDING_VERIFY && ota_fake::rollbacks == 1);
  failed.Tick(120000, false, false, true); Run(failed);
  assert(ota_fake::rollbacks == 1 && ota_fake::confirms == 1);

  ota_fake::Reset(); ota_fake::runningState = ESP_OTA_IMG_PENDING_VERIFY; ota_fake::failTask = true;
  EspIdfFirmwareUpdate noWorker; assert(!noWorker.Begin());
  assert(noWorker.Reserved() && noWorker.Status().error == "ota-worker-unavailable");
  noWorker.Tick(0, false, true, true); noWorker.Tick(5000, false, true, true);
  noWorker.Tick(120000, false, false, true);
  assert(ota_fake::confirms == 0 && ota_fake::rollbacks == 0 && noWorker.Reserved());
  ota_fake::failTask = false;
  assert(!noWorker.Begin() && ota_fake::taskCreates == 1);
  assert(!noWorker.SetAutomaticInstall(true) && !noWorker.LastOperationIsPending());
  ota_fake::Reset(); ota_fake::failMutex = true;
  EspIdfFirmwareUpdate noMutex;
  assert(!noMutex.Reserved() && noMutex.Status().error == "ota-not-started");
  assert(!noMutex.Begin() && noMutex.Reserved() && ota_fake::taskCreates == 0);
  assert(noMutex.Status().state == "service-required" && noMutex.Status().error == "ota-mutex-unavailable");
  ota_fake::failMutex = false;
  assert(!noMutex.Begin() && ota_fake::taskCreates == 0 && ota_fake::guardReads == 0);
  noMutex.Tick(120000, false, true, true);
  assert(noMutex.Reserved() && ota_fake::confirms == 0 && ota_fake::rollbacks == 0);
}
void QueuedConfirmationCannotUseStaleHealth() {
  ota_fake::Reset(); ota_fake::runningState = ESP_OTA_IMG_PENDING_VERIFY;
  EspIdfFirmwareUpdate ota; assert(ota.Begin());
  ota.Tick(0, false, true, true); ota.Tick(5000, false, true, true);
  assert(ota.Reserved() && ota_fake::confirms == 0);
  ota.Tick(5001, false, false, true);
  Run(ota);
  assert(ota.Reserved() && ota_fake::confirms == 0);
  Run(ota);
  assert(ota_fake::rollbacks == 1 && ota.Status().error == "rollback-unavailable");
}
void SettingsIntentAndStalledCommit() {
  ota_fake::Reset();
  EspIdfFirmwareUpdate ota; assert(ota.Begin());
  std::promise<void> entered, release;
  auto enteredFuture = entered.get_future(); auto releaseFuture = release.get_future();
  ota_fake::commitHook = [&]() { entered.set_value(); releaseFuture.wait(); };
  ActualWorker worker;
  assert(!ota.SetAutomaticInstall(true) && ota.LastOperationIsPending());
  assert(enteredFuture.wait_for(std::chrono::seconds(3)) == std::future_status::ready);
  AssertResponsive(ota, 1000, false);
  assert(!ota.Status().automaticInstall && ota_fake::settings.count("auto_install") == 0);
  assert(!ota.SetAutomaticInstall(true) && ota.LastOperationIsPending());
  assert(!ota.SetAutomaticInstall(false) && !ota.LastOperationIsPending());
  assert(!ota.SetCheckIntervalSeconds(60) && !ota.LastOperationIsPending());
  release.set_value();
  WaitUntil([&]() { return ota.Status().automaticInstall; });
  assert(!ota.SetAutomaticInstall(false) && !ota.LastOperationIsPending());
  assert(ota.SetAutomaticInstall(true) && !ota.LastOperationIsPending());
  assert(ota_fake::settings["auto_install"] == "1" && ota_fake::commits == 1);
}
void SettingsFailedAckPreservesPolicy() {
  ota_fake::Reset();
  EspIdfFirmwareUpdate ota; assert(ota.Begin());
  ota_fake::commitSucceeds = false;
  assert(!ota.SetAutomaticInstall(true) && ota.LastOperationIsPending());
  Run(ota);
  assert(!ota.Status().automaticInstall && ota.Status().error == "ota-settings-write-failed");
  assert(!ota.SetAutomaticInstall(true) && !ota.LastOperationIsPending());
  assert(ota_fake::settings.count("auto_install") == 0);
  ota_fake::commitSucceeds = true;
  assert(!ota.SetCheckIntervalSeconds(60) && ota.LastOperationIsPending());
  assert(!ota.SetCheckIntervalSeconds(61) && !ota.LastOperationIsPending());
  Run(ota);
  assert(ota.SetCheckIntervalSeconds(60) && ota.Status().error.empty());
  assert(ota_fake::settings["check_interval"] == "60");
}
void StalledRollbackAndRestartRemainResponsive() {
  ota_fake::Reset(); ota_fake::runningState = ESP_OTA_IMG_PENDING_VERIFY;
  EspIdfFirmwareUpdate rollback; assert(rollback.Begin());
  {
    std::promise<void> entered, release;
    auto enteredFuture = entered.get_future(); auto releaseFuture = release.get_future();
    ota_fake::rollbackHook = [&]() { entered.set_value(); releaseFuture.wait(); };
    ActualWorker worker;
    rollback.Tick(120000, false, false, true);
    assert(enteredFuture.wait_for(std::chrono::seconds(3)) == std::future_status::ready);
    AssertResponsive(rollback, 120001, true);
    release.set_value();
    WaitUntil([&]() { return rollback.Status().error == "rollback-unavailable"; });
  }
  ota_fake::Reset(); Release();
  EspIdfFirmwareUpdate restart; assert(restart.Begin()); Check(restart); StartInstall(restart);
  {
    std::promise<void> entered, release;
    auto enteredFuture = entered.get_future(); auto releaseFuture = release.get_future();
    ota_fake::restartHook = [&]() { entered.set_value(); releaseFuture.wait(); };
    ActualWorker worker;
    restart.Tick(0, false, true, true);
    assert(enteredFuture.wait_for(std::chrono::seconds(3)) == std::future_status::ready);
    AssertResponsive(restart, 0, true);
    assert(!ota_fake::rebooted);
    release.set_value();
  }
  assert(ota_fake::rebooted && restart.Reserved());
}
void RebootPreservingNvsAndSlot() {
  const auto retainedSettings = ota_fake::settings;
  const auto retainedBlobs = ota_fake::blobs;
  const auto retainedState = ota_fake::runningState;
  const auto retainedSlot = ota_fake::running;
  const auto retainedDescription = ota_fake::runningDescription;
  ota_fake::Reset();
  ota_fake::settings = retainedSettings;
  ota_fake::blobs = retainedBlobs;
  ota_fake::runningState = retainedState;
  ota_fake::running = retainedSlot;
  ota_fake::runningDescription = retainedDescription;
}
std::vector<uint8_t> ActualInterruptedGuard() {
  ota_fake::Reset(); ota_fake::runningState = ESP_OTA_IMG_PENDING_VERIFY;
  ota_fake::confirmSucceeds = false;
  EspIdfFirmwareUpdate ota; assert(ota.Begin());
  ota.Tick(0, false, true, true); ota.Tick(5000, false, true, true); Run(ota);
  assert(ota_fake::blobs.size() == 1 && ota_fake::confirms == 1 && ota_fake::guardErases == 0);
  const auto record = ota_fake::blobs.at("c00010000");
  assert(record.size() == 48 && record[0] == 'K' && record[4] == 1);
  assert(record[8] == 0 && record[9] == 0 && record[10] == 1 && record[11] == 0);
  assert(std::equal(record.begin() + 12, record.begin() + 44, ota_fake::runningDescription.app_elf_sha256));
  return record;
}
void ExistingGuardSurvivesSdkValidAndFreshAdapters() {
  const auto record = ActualInterruptedGuard();
  ota_fake::runningState = ESP_OTA_IMG_VALID; // A previous SDK write may have committed without its caller's ACK.
  RebootPreservingNvsAndSlot();
  EspIdfFirmwareUpdate recovered; assert(recovered.Begin() && recovered.Reserved());
  assert(recovered.Status().error == "boot-confirmation-interrupted");
  recovered.Tick(0, false, true, true); recovered.Tick(5000, false, true, true); Run(recovered);
  assert(recovered.Reserved() && recovered.Status().error == "rollback-unavailable");
  assert(ota_fake::confirms == 0 && ota_fake::guardWrites == 0 && ota_fake::guardErases == 0);
  assert(ota_fake::blobs.at("c00010000") == record && ota_fake::runningState == ESP_OTA_IMG_VALID);
  RebootPreservingNvsAndSlot();
  EspIdfFirmwareUpdate again; assert(again.Begin() && again.Reserved());
  assert(again.Status().error == "boot-confirmation-interrupted" && ota_fake::confirms == 0);
}
void GuardReadIntegrityAndSlotIsolation() {
  const auto record = ActualInterruptedGuard();
  for (unsigned failure = 0; failure < 6; ++failure) {
    ota_fake::Reset(); ota_fake::runningState = ESP_OTA_IMG_VALID;
    ota_fake::blobs["c00010000"] = record;
    if (failure == 0) ota_fake::guardReadFails = true;
    if (failure == 1) ota_fake::guardSecondReadFails = true;
    if (failure == 2) ota_fake::blobs["c00010000"].pop_back();
    if (failure == 3) ota_fake::blobs["c00010000"].back() ^= 1; // CRC cut.
    if (failure == 4) ota_fake::runningDescription.app_elf_sha256[0] ^= 1; // Valid record for another image.
    if (failure == 5) {
      ota_fake::running.address = 0x20000;
      ota_fake::blobs["c00020000"] = record; // Valid CRC, wrong address under this slot's key.
    }
    EspIdfFirmwareUpdate held; assert(held.Begin() && held.Reserved());
    assert(held.Status().state == "service-required");
    assert(held.Status().error == (failure < 2 ? "boot-confirmation-guard-unavailable" : "boot-confirmation-guard-invalid"));
    held.Tick(5000, false, true, true); Run(held);
    held.Tick(120001, false, false, true); Run(held);
    assert(held.Reserved() && ota_fake::confirms == 0 && ota_fake::guardWrites == 0 && ota_fake::guardErases == 0);
    assert(ota_fake::rollbacks == 0); // Unknown guard is not authority to invalidate SDK VALID.
    assert(held.Status().state == "service-required");
  }
  for (bool malformed : {false, true}) {
    ota_fake::Reset(); ota_fake::runningState = ESP_OTA_IMG_PENDING_VERIFY;
    if (malformed) ota_fake::blobs["c00010000"] = {1, 2, 3};
    else ota_fake::guardReadFails = true;
    EspIdfFirmwareUpdate pending; assert(pending.Begin() && pending.Reserved());
    pending.Tick(0, false, true, true); Run(pending);
    assert(ota_fake::rollbacks == 1 && ota_fake::confirms == 0 && ota_fake::guardErases == 0);
    assert(pending.Reserved()); // Raw SDK PENDING_VERIFY still owns its normal rollback path.
  }
  ota_fake::Reset(); ota_fake::runningState = ESP_OTA_IMG_VALID;
  ota_fake::blobs["c00010000"] = record;
  ota_fake::running.address = 0x20000;
  ota_fake::runningDescription.app_elf_sha256[0] ^= 1;
  EspIdfFirmwareUpdate otherSlot; assert(otherSlot.Begin() && !otherSlot.Reserved());
  assert(ota_fake::blobs.at("c00010000") == record && ota_fake::guardErases == 0);
}
void GuardPreparationFailuresNeverCallSdkValid() {
  for (unsigned failure = 0; failure < 3; ++failure) {
    ota_fake::Reset(); ota_fake::runningState = ESP_OTA_IMG_PENDING_VERIFY;
    ota_fake::guardWriteFails = failure == 0;
    ota_fake::guardCommitFails = failure == 1;
    ota_fake::guardWriteLostAck = failure == 2;
    EspIdfFirmwareUpdate ota; assert(ota.Begin());
    ota.Tick(0, false, true, true); ota.Tick(5000, false, true, true); Run(ota);
    assert(ota.Reserved() && ota.Status().error == "boot-confirmation-guard-write-failed");
    assert(ota_fake::confirms == 0 && ota_fake::guardErases == 0 && ota_fake::guardWrites == 1);
    assert(ota_fake::blobs.empty() == (failure != 2));
    Run(ota);
    assert(ota_fake::runningState == ESP_OTA_IMG_PENDING_VERIFY && ota.Reserved());
    RebootPreservingNvsAndSlot();
    EspIdfFirmwareUpdate afterReset; assert(afterReset.Begin() && afterReset.Reserved());
    if (failure == 2) {
      assert(afterReset.Status().error == "boot-confirmation-interrupted");
      afterReset.Tick(0, false, true, true); afterReset.Tick(5000, false, true, true); Run(afterReset);
      assert(ota_fake::confirms == 0 && ota_fake::guardErases == 0);
    }
  }
}
void GuardProbeCannotRestampBootDeadline() {
  ota_fake::Reset(); ota_fake::runningState = ESP_OTA_IMG_PENDING_VERIFY;
  ota_fake::guardReadHook = []() { ota_fake::nowMs = 120001; };
  EspIdfFirmwareUpdate ota; assert(ota.Begin());
  ota.Tick(120001, false, true, true); Run(ota);
  assert(ota.Reserved() && ota_fake::rollbacks == 1 && ota_fake::confirms == 0);
}
void GuardPreparationRechecksBeforeSdk() {
  ota_fake::Reset(); ota_fake::runningState = ESP_OTA_IMG_PENDING_VERIFY;
  EspIdfFirmwareUpdate ota; assert(ota.Begin());
  {
    std::promise<void> entered, release;
    auto enteredFuture = entered.get_future(); auto releaseFuture = release.get_future();
    ota_fake::guardPrepareHook = [&]() { entered.set_value(); releaseFuture.wait(); };
    ActualWorker worker;
    ota.Tick(0, false, true, true); ota.Tick(5000, false, true, true);
    assert(enteredFuture.wait_for(std::chrono::seconds(3)) == std::future_status::ready);
    assert(ota_fake::confirms == 0 && ota.Reserved());
    ota_fake::nowMs = 120000;
    ota.Tick(120000, false, false, true);
    AssertResponsive(ota, 120000, true);
    release.set_value();
    WaitUntil([&]() { return ota.Status().error == "rollback-unavailable"; });
    assert(ota_fake::confirms == 0 && ota_fake::guardErases == 0 && !ota_fake::blobs.empty());
  }
  RebootPreservingNvsAndSlot();
  EspIdfFirmwareUpdate recovered; assert(recovered.Begin() && recovered.Reserved());
  assert(recovered.Status().error == "boot-confirmation-interrupted");
}
void LateSdkAckRetainsGuardAcrossReboot() {
  for (bool deadlineExpired : {false, true}) {
    ota_fake::Reset(); ota_fake::runningState = ESP_OTA_IMG_PENDING_VERIFY;
    EspIdfFirmwareUpdate ota; assert(ota.Begin());
    {
      std::promise<void> entered, release;
      auto enteredFuture = entered.get_future(); auto releaseFuture = release.get_future();
      ota_fake::confirmHook = [&]() { entered.set_value(); releaseFuture.wait(); };
      ActualWorker worker;
      ota.Tick(0, false, true, true); ota.Tick(5000, false, true, true);
      assert(enteredFuture.wait_for(std::chrono::seconds(3)) == std::future_status::ready);
      assert(ota_fake::blobs.size() == 1 && ota_fake::guardErases == 0);
      ota_fake::nowMs = deadlineExpired ? 120000 : 5001;
      ota.Tick(ota_fake::nowMs, false, !deadlineExpired ? false : true, true);
      AssertResponsive(ota, ota_fake::nowMs, true);
      release.set_value();
      WaitUntil([&]() { return ota.Status().error == "rollback-unavailable"; });
      assert(ota.Reserved() && ota_fake::runningState == ESP_OTA_IMG_VALID && ota_fake::guardErases == 0);
    }
    RebootPreservingNvsAndSlot();
    EspIdfFirmwareUpdate recovered; assert(recovered.Begin() && recovered.Reserved());
    recovered.Tick(0, false, true, true); recovered.Tick(5000, false, true, true); Run(recovered);
    assert(recovered.Reserved() && ota_fake::confirms == 0 && ota_fake::guardErases == 0);
    assert(ota_fake::blobs.size() == 1);
  }
}
void AuthorizedGuardEraseIsNotRevokedByLaterHealthOrDeadline() {
  ota_fake::Reset(); ota_fake::runningState = ESP_OTA_IMG_PENDING_VERIFY;
  EspIdfFirmwareUpdate ota; assert(ota.Begin());
  {
    std::promise<void> entered, release;
    auto enteredFuture = entered.get_future(); auto releaseFuture = release.get_future();
    ota_fake::guardClearHook = [&]() { entered.set_value(); releaseFuture.wait(); };
    ActualWorker worker;
    ota.Tick(0, false, true, true); ota.Tick(5000, false, true, true);
    assert(enteredFuture.wait_for(std::chrono::seconds(3)) == std::future_status::ready);
    assert(ota.Status().state == "finalizing-boot" && ota.Reserved() && ota_fake::blobs.size() == 1);
    assert(ota_fake::runningState == ESP_OTA_IMG_VALID);
    ota_fake::nowMs = 120001;
    ota.Tick(120001, false, false, true);
    AssertResponsive(ota, 120001, true);
    assert(ota_fake::rollbacks == 0);
    release.set_value();
    WaitUntil([&]() { return !ota.Reserved(); });
    assert(ota_fake::blobs.empty() && ota_fake::rollbacks == 0 && ota.Status().state == "idle");
  }
  RebootPreservingNvsAndSlot();
  EspIdfFirmwareUpdate recovered; assert(recovered.Begin() && !recovered.Reserved());
  assert(ota_fake::confirms == 0 && ota_fake::guardWrites == 0 && ota_fake::guardErases == 0);
}
void GuardClearFailuresAndLostAckStayHonest() {
  for (unsigned failure = 0; failure < 3; ++failure) {
    ota_fake::Reset(); ota_fake::runningState = ESP_OTA_IMG_PENDING_VERIFY;
    ota_fake::guardEraseFails = failure == 0;
    ota_fake::guardEraseCommitFails = failure == 1;
    ota_fake::guardEraseLostAck = failure == 2;
    EspIdfFirmwareUpdate ota; assert(ota.Begin());
    ota.Tick(0, false, true, true); ota.Tick(5000, false, true, true); Run(ota);
    assert(ota.Reserved() && ota.Status().state == "service-required");
    assert(ota.Status().error == "boot-confirmation-guard-clear-failed" && ota_fake::runningState == ESP_OTA_IMG_VALID);
    assert(ota_fake::guardErases == 1 && ota_fake::confirms == 1);
    assert(ota_fake::blobs.empty() == (failure == 2));
    ota.Tick(120001, false, false, true); Run(ota);
    assert(ota.Reserved() && ota_fake::rollbacks == 0 && ota_fake::guardErases == 1);
    RebootPreservingNvsAndSlot();
    EspIdfFirmwareUpdate recovered; assert(recovered.Begin());
    assert(recovered.Reserved() == (failure != 2));
    assert(ota_fake::confirms == 0 && ota_fake::guardErases == 0);
    if (failure == 2) assert(recovered.Status().state == "idle");
    else assert(recovered.Status().error == "boot-confirmation-interrupted");
  }
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
  health.Tick(0, false, true, true); health.Tick(5000, false, true, true); Run(health);
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
  unhealthy.Tick(120000, false, false, true); Run(unhealthy);
  assert(ota_fake::rolledBack && unhealthy.Reserved());
}
}
int main() {
  Success(); TransferBufferAndExactLength(); Failures(); UnattendedBootComposition(); PassiveBootFailuresAndReset(); BootHealth();
  DeferredBootActions(); OriginalBootDeadlineAndRollbackFailure(); ConfirmationFailureAndWorkerAllocationFailure();
  QueuedConfirmationCannotUseStaleHealth(); SettingsIntentAndStalledCommit(); SettingsFailedAckPreservesPolicy(); StalledRollbackAndRestartRemainResponsive();
  ExistingGuardSurvivesSdkValidAndFreshAdapters(); GuardReadIntegrityAndSlotIsolation();
  GuardPreparationFailuresNeverCallSdkValid(); GuardProbeCannotRestampBootDeadline(); GuardPreparationRechecksBeforeSdk(); LateSdkAckRetainsGuardAcrossReboot();
  AuthorizedGuardEraseIsNotRevokedByLaterHealthOrDeadline(); GuardClearFailuresAndLostAckStayHonest();
  std::cout << "ESP-IDF OTA adapter success/failure, safe reservation and boot recovery tests passed (fake HTTP/flash/crypto).\n";
}

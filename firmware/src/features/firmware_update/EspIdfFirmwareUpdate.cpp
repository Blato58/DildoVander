#include "EspIdfFirmwareUpdate.h"

#include <algorithm>
#include <array>
#include <cstring>
#include <cstdio>
#include <memory>
#include <new>
#include "esp_app_desc.h"
#include "esp_crt_bundle.h"
#include "esp_http_client.h"
#include "esp_ota_ops.h"
#include "esp_partition.h"
#include "esp_system.h"
#include "esp_timer.h"
#include "mbedtls/pk.h"
#include "nvs.h"
#include "psa/crypto.h"

#ifndef KUFR_FIRMWARE_VERSION
#define KUFR_FIRMWARE_VERSION "0.1.0"
#endif
#ifndef KUFR_FIRMWARE_BUILD_ID
#define KUFR_FIRMWARE_BUILD_ID "0000000000000000000000000000000000000000"
#endif

namespace kufr { namespace ota {
namespace {
constexpr uint64_t DownloadDeadlineMs = 180000;
constexpr size_t TransferBufferSize = 4096;
uint32_t GuardCrc(const uint8_t* bytes, size_t length) {
  uint32_t crc = 0xffffffffu;
  for (size_t i = 0; i < length; ++i) {
    crc ^= bytes[i];
    for (unsigned bit = 0; bit < 8; ++bit) crc = (crc >> 1) ^ ((crc & 1u) ? 0xedb88320u : 0u);
  }
  return ~crc;
}
void StoreLe32(uint8_t* bytes, uint32_t value) {
  for (unsigned n = 0; n < 4; ++n) bytes[n] = static_cast<uint8_t>(value >> (n * 8));
}
uint64_t NowMs() { return static_cast<uint64_t>(esp_timer_get_time() / 1000); }

class Lock final {
 public:
  explicit Lock(SemaphoreHandle_t mutex) : mutex_(mutex) { xSemaphoreTake(mutex_, portMAX_DELAY); }
  ~Lock() { xSemaphoreGive(mutex_); }
 private:
  SemaphoreHandle_t mutex_;
};
bool ReadText(nvs_handle_t nvs, const char* name, std::string& value, size_t maximum) {
  size_t length = 0;
  if (nvs_get_str(nvs, name, nullptr, &length) != ESP_OK || length < 2 || length > maximum + 1) return false;
  std::vector<char> buffer(length);
  if (nvs_get_str(nvs, name, buffer.data(), &length) != ESP_OK || buffer.back() != '\0') return false;
  value.assign(buffer.data(), length - 1);
  return value.find('\0') == std::string::npos;
}

class PublicKey final {
 public:
  PublicKey() { mbedtls_pk_init(&key); }
  ~PublicKey() { mbedtls_pk_free(&key); }
  bool Parse(const std::string& pem) {
    if (psa_crypto_init() != PSA_SUCCESS ||
        mbedtls_pk_parse_public_key(&key, reinterpret_cast<const uint8_t*>(pem.c_str()), pem.size() + 1) != 0) return false;
    psa_key_attributes_t attributes = PSA_KEY_ATTRIBUTES_INIT;
    bool valid = mbedtls_pk_get_psa_attributes(&key, PSA_KEY_USAGE_VERIFY_HASH, &attributes) == 0 &&
        psa_get_key_type(&attributes) == PSA_KEY_TYPE_ECC_PUBLIC_KEY(PSA_ECC_FAMILY_SECP_R1) &&
        psa_get_key_bits(&attributes) == 256;
    psa_reset_key_attributes(&attributes);
    return valid;
  }
  mbedtls_pk_context key;
};

class Http final {
 public:
  ~Http() { if (client) esp_http_client_cleanup(client); }
  bool Open(const std::string& url, size_t maximum, uint32_t exact = 0) {
    esp_http_client_config_t config{};
    config.url = url.c_str();
    config.crt_bundle_attach = esp_crt_bundle_attach;
    config.disable_auto_redirect = true;
    config.skip_cert_common_name_check = false;
    config.timeout_ms = 5000;
    config.max_authorization_retries = -1;
    config.buffer_size = 4096;
    client = esp_http_client_init(&config);
    if (!client || esp_http_client_open(client, 0) != ESP_OK) return false;
    const int64_t length = esp_http_client_fetch_headers(client);
    // open/fetch_headers never follows a Location. esp_https_ota does follow
    // redirects internally even with disable_auto_redirect in ESP-IDF 6.1.
    if (esp_http_client_get_status_code(client) != 200 || length <= 0 ||
        static_cast<uint64_t>(length) > maximum || esp_http_client_is_chunked_response(client) ||
        (exact && length != exact)) return false;
    contentLength = static_cast<uint32_t>(length);
    return true;
  }
  esp_http_client_handle_t client = nullptr;
  uint32_t contentLength = 0;
};

std::string DigestHex(const std::array<uint8_t, 32>& digest) {
  const char* digits = "0123456789abcdef";
  std::string result;
  result.reserve(64);
  for (auto byte : digest) { result.push_back(digits[byte >> 4]); result.push_back(digits[byte & 15]); }
  return result;
}
bool PreviouslyFailed(const esp_partition_t* target, const Manifest& candidate) {
  esp_ota_img_states_t state;
  esp_app_desc_t description{};
  return esp_ota_get_state_partition(target, &state) == ESP_OK &&
      (state == ESP_OTA_IMG_INVALID || state == ESP_OTA_IMG_ABORTED) &&
      esp_ota_get_partition_description(target, &description) == ESP_OK &&
      std::strncmp(description.version, candidate.version.c_str(), sizeof(description.version)) == 0;
}
}  // namespace

bool EspIdfFirmwareUpdate::LoadSettings() {
  nvs_handle_t nvs = 0;
  if (nvs_open("wand_ota", NVS_READONLY, &nvs) != ESP_OK) return false;
  const bool found = ReadText(nvs, "origin", origin_, 267) &&
      ReadText(nvs, "trust_pem", trustPem_, 1024);
  if (!ReadText(nvs, "channel", channel_, 16)) channel_ = "stable";
  uint8_t automatic = 0;
  nvs_get_u8(nvs, "auto_install", &automatic);
  uint32_t checkInterval = 3600;
  nvs_get_u32(nvs, "check_interval", &checkInterval);
  if (!policy_.SetCheckIntervalSeconds(checkInterval)) policy_.SetCheckIntervalSeconds(3600);
  nvs_close(nvs);
  PublicKey key;
  status_.automaticInstall = automatic == 1;
  return found && IsApprovedOrigin(origin_) && (channel_ == "stable" || channel_ == "development") && key.Parse(trustPem_);
}

EspIdfFirmwareUpdate::GuardRead EspIdfFirmwareUpdate::LoadConfirmationGuard() {
  const auto* running = esp_ota_get_running_partition();
  const auto* description = esp_app_get_description();
  if (!running || !description) return GuardRead::Unavailable;
  // Fixed canonical bytes, never a native struct: retain the exact image/slot
  // identity across SDK VALID and a reset before the authorized guard erase.
  confirmationGuard_[0] = 'K'; confirmationGuard_[1] = 'O';
  confirmationGuard_[2] = 'C'; confirmationGuard_[3] = 'G';
  confirmationGuard_[4] = 1;
  StoreLe32(confirmationGuard_.data() + 8, running->address);
  std::copy_n(description->app_elf_sha256, 32, confirmationGuard_.data() + 12);
  StoreLe32(confirmationGuard_.data() + 44, GuardCrc(confirmationGuard_.data(), 44));
  std::snprintf(confirmationGuardKey_.data(), confirmationGuardKey_.size(), "c%08lx",
      static_cast<unsigned long>(running->address));
  nvs_handle_t nvs = 0;
  const auto opened = nvs_open("wand_ota", NVS_READONLY, &nvs);
  if (opened == ESP_ERR_NVS_NOT_FOUND) return GuardRead::Missing;
  if (opened != ESP_OK) return GuardRead::Unavailable;
  size_t length = 0;
  auto read = nvs_get_blob(nvs, confirmationGuardKey_.data(), nullptr, &length);
  if (read == ESP_ERR_NVS_NOT_FOUND) { nvs_close(nvs); return GuardRead::Missing; }
  if (read != ESP_OK) { nvs_close(nvs); return GuardRead::Unavailable; }
  if (length != confirmationGuard_.size()) { nvs_close(nvs); return GuardRead::Invalid; }
  std::array<uint8_t, 48> stored{};
  read = nvs_get_blob(nvs, confirmationGuardKey_.data(), stored.data(), &length);
  nvs_close(nvs);
  if (read != ESP_OK) return GuardRead::Unavailable;
  return length == stored.size() && stored == confirmationGuard_ ? GuardRead::Present : GuardRead::Invalid;
}
bool EspIdfFirmwareUpdate::PersistConfirmationGuard(bool erase) {
  nvs_handle_t nvs = 0;
  if (nvs_open("wand_ota", NVS_READWRITE, &nvs) != ESP_OK) return false;
  const auto changed = erase ? nvs_erase_key(nvs, confirmationGuardKey_.data())
      : nvs_set_blob(nvs, confirmationGuardKey_.data(), confirmationGuard_.data(), confirmationGuard_.size());
  const bool stored = changed == ESP_OK && nvs_commit(nvs) == ESP_OK;
  nvs_close(nvs);
  return stored;
}

bool EspIdfFirmwareUpdate::Begin() {
  if (beginAttempted_) return false;
  beginAttempted_ = true;
  mutex_ = xSemaphoreCreateMutex();
  if (!mutex_) { reserved_ = true; return false; }
  const uint64_t startedAt = NowMs();
  status_.version = KUFR_FIRMWARE_VERSION;
  const esp_partition_t* running = esp_ota_get_running_partition();
  esp_ota_img_states_t bootState;
  bootPending_ = running && esp_ota_get_state_partition(running, &bootState) == ESP_OK && bootState == ESP_OTA_IMG_PENDING_VERIFY;
  const auto guard = LoadConfirmationGuard();
  confirmationBlocked_ = guard != GuardRead::Missing;
  rollbackAllowed_ = bootPending_ || guard == GuardRead::Present;
  bootPending_ = bootPending_ || confirmationBlocked_;
  rollbackRequested_ = confirmationBlocked_ && rollbackAllowed_;
  reserved_ = bootPending_;
  bootDeadlineMs_ = startedAt + OtaPolicy::BootDeadlineMs;
  policy_.Start(startedAt, bootPending_);
  configured_ = LoadSettings();
  status_.state = bootPending_ ? "verifying-boot" : configured_ ? "idle" : "disabled";
  if (!configured_) status_.error = "ota-trust-or-origin-not-provisioned";
  if (confirmationBlocked_) {
    status_.state = "service-required";
    status_.error = guard == GuardRead::Present ? "boot-confirmation-interrupted"
        : guard == GuardRead::Invalid ? "boot-confirmation-guard-invalid" : "boot-confirmation-guard-unavailable";
  }
  if (!configured_ && !bootPending_) return true;
  if (xTaskCreate(WorkerEntry, "kufr-ota", 8192, this, 3, &task_) != pdPASS) {
    configured_ = false;
    status_.state = bootPending_ ? "verifying-boot" : "disabled";
    status_.error = "ota-worker-unavailable";
    return false;
  }
  return true;
}

StatusSnapshot EspIdfFirmwareUpdate::Status() const {
  if (!mutex_) return {beginAttempted_ ? "service-required" : "disabled", KUFR_FIRMWARE_VERSION, {},
      beginAttempted_ ? "ota-mutex-unavailable" : "ota-not-started", false};
  Lock lock(mutex_);
  return status_;
}
bool EspIdfFirmwareUpdate::Reserved() const {
  if (!mutex_) return reserved_;
  Lock lock(mutex_);
  return reserved_;
}
bool EspIdfFirmwareUpdate::RequestCheck() {
  if (!mutex_) return false;
  Lock lock(mutex_);
  if (!configured_ || bootPending_ || reserved_ || work_ != Work::None || status_.state == "checking") return false;
  checkRequested_ = true;
  return true;
}
bool EspIdfFirmwareUpdate::RequestInstall() {
  if (!mutex_) return false;
  Lock lock(mutex_);
  if (!configured_ || bootPending_ || reserved_ ||
      (status_.state != "available" && status_.state != "waiting-idle")) return false;
  installRequested_ = true;
  status_.state = "waiting-idle";
  return true;
}
bool EspIdfFirmwareUpdate::SetAutomaticInstall(bool enabled) {
  return SetSetting(Setting::AutomaticInstall, enabled ? 1u : 0u);
}
bool EspIdfFirmwareUpdate::SetCheckIntervalSeconds(uint32_t seconds) {
  if (seconds < 60 || seconds > 86400) {
    if (mutex_) { Lock lock(mutex_); lastOperationPending_ = false; }
    return false;
  }
  return SetSetting(Setting::CheckInterval, seconds);
}
bool EspIdfFirmwareUpdate::LastOperationIsPending() const {
  if (!mutex_) return false;
  Lock lock(mutex_);
  return lastOperationPending_;
}
bool EspIdfFirmwareUpdate::SetSetting(Setting kind, uint32_t value) {
  if (!mutex_) return false;
  {
    Lock lock(mutex_);
    lastOperationPending_ = false;
    if (settings_.active) {
      if (settings_.kind != kind || settings_.value != value) return false;
      if (settings_.result == SettingResult::Pending) { lastOperationPending_ = true; return false; }
      const bool stored = settings_.result == SettingResult::Succeeded;
      settings_ = {};
      return stored;
    }
    if (!configured_ || !task_ || bootPending_ || reserved_ || work_ != Work::None) return false;
    settings_.active = true;
    settings_.kind = kind;
    settings_.value = value;
    work_ = Work::Settings;
    lastOperationPending_ = true;
  }
  xTaskNotifyGive(task_);
  return false;
}
void EspIdfFirmwareUpdate::StoreSettings() {
  SettingsIntent intent;
  { Lock lock(mutex_); intent = settings_; }
  nvs_handle_t nvs = 0;
  bool stored = nvs_open("wand_ota", NVS_READWRITE, &nvs) == ESP_OK;
  if (stored) {
    stored = (intent.kind == Setting::AutomaticInstall
        ? nvs_set_u8(nvs, "auto_install", static_cast<uint8_t>(intent.value))
        : nvs_set_u32(nvs, "check_interval", intent.value)) == ESP_OK && nvs_commit(nvs) == ESP_OK;
    nvs_close(nvs);
  }
  Lock lock(mutex_);
  settings_.result = stored ? SettingResult::Succeeded : SettingResult::Failed;
  if (!stored) status_.error = "ota-settings-write-failed";
  else {
    if (status_.error == "ota-settings-write-failed") status_.error.clear();
    if (intent.kind == Setting::AutomaticInstall) status_.automaticInstall = intent.value != 0;
    else { policy_.SetCheckIntervalSeconds(intent.value); policy_.Checked(NowMs(), true); }
  }
}

void EspIdfFirmwareUpdate::Tick(uint64_t nowMs, bool networkReady, bool selfTestReady, bool safeIdle) {
  if (!mutex_) return;
  bool notify = false;
  {
    Lock lock(mutex_);
    // Keep the original deadline even after BootHealth consumes its Confirm
    // decision. A slow/failed flash ACK must never start another health window.
    if (bootPending_ && !finalizationAuthorized_) {
      const auto decision = confirmationBlocked_ ? BootDecision::Wait : policy_.BootHealth(nowMs, selfTestReady);
      if (rollbackAllowed_ && !rollbackAttempted_ && (decision == BootDecision::Rollback || nowMs >= bootDeadlineMs_ ||
          (work_ == Work::ConfirmBoot && !selfTestReady))) rollbackRequested_ = true;
      if (task_ && work_ == Work::None) {
        if (rollbackRequested_) { work_ = Work::Rollback; status_.state = "rollback"; notify = true; }
        else if (decision == BootDecision::Confirm) { work_ = Work::ConfirmBoot; notify = true; }
      }
    }
    if (!bootPending_ && configured_ && task_ && work_ == Work::None) {
      if (status_.state == "ready-to-reboot" && safeIdle) {
        work_ = Work::Reboot;
        status_.state = "rebooting";
        notify = true;
      } else if ((status_.state == "available" || status_.state == "waiting-idle") &&
          (installRequested_ || status_.automaticInstall)) {
        status_.state = "waiting-idle";
        if (networkReady && safeIdle) {
          reserved_ = true;
          status_.state = "downloading";
          installRequested_ = false;
          work_ = Work::Install;
          notify = true;
        }
      } else if (networkReady && safeIdle &&
          (checkRequested_ || policy_.CheckDue(nowMs, networkReady, safeIdle)) &&
          status_.state != "checking" && !reserved_) {
        checkRequested_ = false;
        status_.state = "checking";
        work_ = Work::Check;
        notify = true;
      }
    }
  }
  if (notify) xTaskNotifyGive(task_);
}
void EspIdfFirmwareUpdate::ConfirmBoot() {
  const auto reject = [this](const char* error) {
    Lock lock(mutex_);
    confirmationBlocked_ = true;
    rollbackRequested_ = true;
    status_.state = "rollback";
    status_.error = error;
  };
  bool attempt;
  { Lock lock(mutex_); attempt = !confirmationBlocked_ && !rollbackRequested_ && NowMs() < bootDeadlineMs_; }
  if (!attempt) { reject("boot-confirmation-expired"); return; }
  // A failure ACK can still hide a durable write. Never call SDK VALID unless
  // the guard is confirmed; any retained/unknown guard is rollback/service only.
  if (!PersistConfirmationGuard(false)) { reject("boot-confirmation-guard-write-failed"); return; }
  { Lock lock(mutex_); attempt = !rollbackRequested_ && NowMs() < bootDeadlineMs_; }
  if (!attempt) { reject("boot-confirmation-expired"); return; }
  const bool valid = esp_ota_mark_app_valid_cancel_rollback() == ESP_OK;
  bool authorized = false;
  {
    Lock lock(mutex_);
    if (valid && !rollbackRequested_ && NowMs() < bootDeadlineMs_) {
      finalizationAuthorized_ = authorized = true;
      status_.state = "finalizing-boot";
    }
  }
  if (!authorized) { reject(valid ? "boot-confirmation-expired" : "boot-confirmation-failed"); return; }
  // The seal above is final. Rechecking health after an authorized erase could
  // leave SDK VALID with no durable guard and an authority that exists only RAM.
  const bool erased = PersistConfirmationGuard(true);
  Lock lock(mutex_);
  if (erased) {
    bootPending_ = false;
    reserved_ = false;
    status_.state = configured_ ? "idle" : "disabled";
    status_.error.clear();
  } else {
    confirmationBlocked_ = true;
    status_.state = "service-required";
    status_.error = "boot-confirmation-guard-clear-failed";
  }
}
void EspIdfFirmwareUpdate::Rollback() {
  { Lock lock(mutex_); rollbackAttempted_ = true; }
  esp_ota_mark_app_invalid_rollback_and_reboot();
  // Returning means the device did not reboot. Never retry or silently confirm.
  Lock lock(mutex_);
  rollbackRequested_ = false;
  status_.state = "rollback";
  status_.error = "rollback-unavailable";
}
void EspIdfFirmwareUpdate::WorkerEntry(void* owner) { static_cast<EspIdfFirmwareUpdate*>(owner)->Worker(); }
void EspIdfFirmwareUpdate::ProcessWork() {
  Work work;
  { Lock lock(mutex_); work = work_; }
  switch (work) {
    case Work::Check: Check(); break;
    case Work::Install: Install(); break;
    case Work::ConfirmBoot: ConfirmBoot(); break;
    case Work::Rollback: Rollback(); break;
    case Work::Reboot: esp_restart(); break;
    case Work::Settings: StoreSettings(); break;
    case Work::None: break;
  }
  bool rollback;
  {
    Lock lock(mutex_);
    rollback = bootPending_ && rollbackRequested_ && work != Work::Rollback;
    work_ = rollback ? Work::Rollback : Work::None;
  }
  if (rollback) xTaskNotifyGive(task_);
}
void EspIdfFirmwareUpdate::Worker() {
  for (;;) {
    ulTaskNotifyTake(pdTRUE, portMAX_DELAY);
    ProcessWork();
  }
}
void EspIdfFirmwareUpdate::Fail(const char* error) {
  Lock lock(mutex_);
  status_.state = "error";
  status_.error = error;
  status_.availableVersion.clear();
  manifest_ = {};
  reserved_ = false;
  installRequested_ = false;
  policy_.Checked(NowMs(), false);
}

bool EspIdfFirmwareUpdate::VerifyEnvelope(const Envelope& envelope) const {
  PublicKey key;
  std::array<uint8_t, 32> hash{};
  size_t length = 0;
  return key.Parse(trustPem_) &&
      psa_hash_compute(PSA_ALG_SHA_256, reinterpret_cast<const uint8_t*>(envelope.payload.data()),
                       envelope.payload.size(), hash.data(), hash.size(), &length) == PSA_SUCCESS &&
      length == hash.size() &&
      mbedtls_pk_verify(&key.key, MBEDTLS_MD_SHA256, hash.data(), hash.size(),
                        envelope.signature.data(), envelope.signature.size()) == 0;
}

void EspIdfFirmwareUpdate::Check() {
  Http http;
  if (!http.Open(origin_ + "/v1/firmware/manifests/" + channel_ + ".json", MaximumManifestBytes)) { Fail("manifest-http-failed"); return; }
  std::string json;
  std::array<char, 1024> buffer{};
  const uint64_t deadline = NowMs() + 20000;
  while (json.size() < http.contentLength && NowMs() < deadline) {
    const int received = esp_http_client_read(http.client, buffer.data(),
        static_cast<int>(std::min(buffer.size(), static_cast<size_t>(http.contentLength - json.size()))));
    if (received <= 0) break;
    json.append(buffer.data(), received);
    vTaskDelay(1);
  }
  Envelope envelope;
  Manifest candidate;
  if (json.size() != http.contentLength || !esp_http_client_is_complete_data_received(http.client)) { Fail("manifest-incomplete"); return; }
  if (!ParseEnvelope(json, envelope) || !VerifyEnvelope(envelope)) { Fail("manifest-signature-invalid"); return; }
  if (!ParseManifest(envelope.payload, candidate)) { Fail("manifest-invalid"); return; }
  const esp_partition_t* target = esp_ota_get_next_update_partition(nullptr);
  const esp_partition_t* running = esp_ota_get_running_partition();
  if (!target || !running || target->address == running->address) { Fail("inactive-slot-unavailable"); return; }
  if (PreviouslyFailed(target, candidate)) { Fail("previously-failed-version"); return; }
  const std::string compatible = ValidateUpgrade(candidate, KUFR_FIRMWARE_VERSION,
      KUFR_FIRMWARE_BUILD_ID, channel_, target->size);
  if (!compatible.empty() && compatible != "no-update") { Fail(compatible.c_str()); return; }
  Lock lock(mutex_);
  status_.error.clear();
  policy_.Checked(NowMs(), true);
  manifest_ = compatible.empty() ? candidate : Manifest{};
  status_.availableVersion = compatible.empty() ? candidate.version : std::string{};
  status_.state = compatible.empty() ? "available" : "current";
}

bool EspIdfFirmwareUpdate::VerifyPartition(const void* partition, const Manifest& manifest,
                                         uint8_t* buffer, size_t bufferSize) const {
  // Hash exactly the signed artifact length, including image padding/appended
  // image hash. esp_partition_get_sha256 has different app-image semantics.
  const auto* target = static_cast<const esp_partition_t*>(partition);
  if (!buffer || !bufferSize) return false;
  psa_hash_operation_t hash = PSA_HASH_OPERATION_INIT;
  if (psa_hash_setup(&hash, PSA_ALG_SHA_256) != PSA_SUCCESS) return false;
  bool valid = true;
  for (uint32_t offset = 0; offset < manifest.sizeBytes && valid;) {
    const size_t count = std::min(bufferSize, static_cast<size_t>(manifest.sizeBytes - offset));
    valid = esp_partition_read(target, offset, buffer, count) == ESP_OK &&
        psa_hash_update(&hash, buffer, count) == PSA_SUCCESS;
    offset += static_cast<uint32_t>(count);
    vTaskDelay(1);
  }
  std::array<uint8_t, 32> digest{};
  size_t length = 0;
  valid = valid && psa_hash_finish(&hash, digest.data(), digest.size(), &length) == PSA_SUCCESS &&
      length == digest.size() && DigestHex(digest) == manifest.sha256;
  psa_hash_abort(&hash);
  return valid;
}

void EspIdfFirmwareUpdate::Install() {
  Manifest candidate;
  { Lock lock(mutex_); candidate = manifest_; }
  const esp_partition_t* target = esp_ota_get_next_update_partition(nullptr);
  const esp_partition_t* running = esp_ota_get_running_partition();
  if (!target || !running || target->address == running->address ||
      PreviouslyFailed(target, candidate) ||
      !ValidateUpgrade(candidate, KUFR_FIRMWARE_VERSION, KUFR_FIRMWARE_BUILD_ID, channel_, target->size).empty()) {
    Fail("install-compatibility-changed"); return;
  }
  // Download and flash verification share one allocation, acquired before any
  // erase/write begins. Nested 4 KiB automatic buffers exhaust the worker stack.
  std::unique_ptr<uint8_t[]> buffer(new (std::nothrow) uint8_t[TransferBufferSize]);
  if (!buffer) { Fail("update-buffer-unavailable"); return; }
  Http http;
  if (!http.Open(origin_ + candidate.url, target->size, candidate.sizeBytes)) { Fail("artifact-http-failed"); return; }
  esp_ota_handle_t handle = 0;
  if (esp_ota_begin(target, candidate.sizeBytes, &handle) != ESP_OK) { Fail("flash-begin-failed"); return; }
  uint32_t written = 0;
  const uint64_t deadline = NowMs() + DownloadDeadlineMs;
  bool valid = true;
  while (written < candidate.sizeBytes && valid && NowMs() < deadline) {
    const int received = esp_http_client_read(http.client, reinterpret_cast<char*>(buffer.get()),
        static_cast<int>(std::min(TransferBufferSize, static_cast<size_t>(candidate.sizeBytes - written))));
    valid = received > 0 && esp_ota_write(handle, buffer.get(), received) == ESP_OK;
    if (valid) written += static_cast<uint32_t>(received);
    vTaskDelay(1);
  }
  if (!valid || written != candidate.sizeBytes || !esp_http_client_is_complete_data_received(http.client)) {
    esp_ota_abort(handle); Fail("artifact-download-incomplete"); return;
  }
  // End verifies ESP image structure/chip and releases write buffers without
  // changing boot selection. Only our digest check can promote the slot.
  if (esp_ota_end(handle) != ESP_OK) { Fail("artifact-image-invalid"); return; }
  { Lock lock(mutex_); status_.state = "verifying-image"; }
  if (!VerifyPartition(target, candidate, buffer.get(), TransferBufferSize)) { Fail("artifact-sha256-invalid"); return; }
  esp_app_desc_t description{};
  if (esp_ota_get_partition_description(target, &description) != ESP_OK ||
      std::strncmp(description.version, candidate.version.c_str(), sizeof(description.version)) != 0 ||
      std::strncmp(description.project_name, esp_app_get_description()->project_name, sizeof(description.project_name)) != 0) {
    Fail("artifact-description-mismatch"); return;
  }
  if (esp_ota_set_boot_partition(target) != ESP_OK) { Fail("boot-slot-promotion-failed"); return; }
  Lock lock(mutex_);
  status_.state = "ready-to-reboot";
  status_.error.clear();
  // Reservation stays held until the control owner safely reboots.
}
} }  // namespace kufr::ota

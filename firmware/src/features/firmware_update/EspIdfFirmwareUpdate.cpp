#include "EspIdfFirmwareUpdate.h"

#include <algorithm>
#include <array>
#include <cstring>
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

bool EspIdfFirmwareUpdate::Begin() {
  if (mutex_) return false;
  mutex_ = xSemaphoreCreateMutex();
  if (!mutex_) return false;
  status_.version = KUFR_FIRMWARE_VERSION;
  const esp_partition_t* running = esp_ota_get_running_partition();
  esp_ota_img_states_t bootState;
  bootPending_ = running && esp_ota_get_state_partition(running, &bootState) == ESP_OK && bootState == ESP_OTA_IMG_PENDING_VERIFY;
  reserved_ = bootPending_;
  policy_.Start(NowMs(), bootPending_);
  configured_ = LoadSettings();
  status_.state = bootPending_ ? "verifying-boot" : configured_ ? "idle" : "disabled";
  if (!configured_) status_.error = "ota-trust-or-origin-not-provisioned";
  if (!configured_) return true;  // Rollback health still runs without settings.
  if (xTaskCreate(WorkerEntry, "kufr-ota", 8192, this, 3, &task_) != pdPASS) {
    configured_ = false;
    status_.state = "disabled";
    status_.error = "ota-worker-unavailable";
    return false;
  }
  return true;
}

StatusSnapshot EspIdfFirmwareUpdate::Status() const {
  if (!mutex_) return {"disabled", KUFR_FIRMWARE_VERSION, {}, "ota-not-started", false};
  Lock lock(mutex_);
  return status_;
}
bool EspIdfFirmwareUpdate::Reserved() const {
  if (!mutex_) return false;
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
  if (!mutex_) return false;
  Lock lock(mutex_);
  if (!configured_) return false;
  nvs_handle_t nvs = 0;
  if (nvs_open("wand_ota", NVS_READWRITE, &nvs) != ESP_OK) return false;
  const bool stored = nvs_set_u8(nvs, "auto_install", enabled ? 1 : 0) == ESP_OK && nvs_commit(nvs) == ESP_OK;
  nvs_close(nvs);
  if (stored) status_.automaticInstall = enabled;
  return stored;
}
bool EspIdfFirmwareUpdate::SetCheckIntervalSeconds(uint32_t seconds) {
  if (!mutex_ || seconds < 60 || seconds > 86400) return false;
  Lock lock(mutex_);
  if (!configured_) return false;
  nvs_handle_t nvs = 0;
  if (nvs_open("wand_ota", NVS_READWRITE, &nvs) != ESP_OK) return false;
  const bool stored = nvs_set_u32(nvs, "check_interval", seconds) == ESP_OK && nvs_commit(nvs) == ESP_OK;
  nvs_close(nvs);
  if (stored) { policy_.SetCheckIntervalSeconds(seconds); policy_.Checked(NowMs(), true); }
  return stored;
}

void EspIdfFirmwareUpdate::Tick(uint64_t nowMs, bool networkReady, bool selfTestReady, bool safeIdle) {
  if (!mutex_) return;
  bool reboot = false, rollback = false, confirm = false, notify = false;
  {
    Lock lock(mutex_);
    // A locally healthy image must survive an unattended boot without a hotspot.
    // Network readiness still gates every manifest check and download below.
    const auto decision = policy_.BootHealth(nowMs, selfTestReady);
    if (decision == BootDecision::Rollback) { rollback = true; reserved_ = true; status_.state = "rollback"; }
    if (decision == BootDecision::Confirm) confirm = true;
    if (!bootPending_ && configured_ && work_ == Work::None) {
      if (status_.state == "ready-to-reboot" && safeIdle) reboot = true;
      else if ((status_.state == "available" || status_.state == "waiting-idle") &&
          (installRequested_ || status_.automaticInstall)) {
        status_.state = "waiting-idle";
        if (networkReady && safeIdle) {
          // Publication and admission checks share the control owner's loop.
          // No other task may start a conversation/effect while Reserved.
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
  if (confirm) {
    const bool valid = esp_ota_mark_app_valid_cancel_rollback() == ESP_OK;
    Lock lock(mutex_);
    bootPending_ = !valid;
    if (valid) { reserved_ = false; status_.state = configured_ ? "idle" : "disabled"; status_.error.clear(); }
    else {
      // A failed flash metadata commit must retain a live rollback deadline.
      status_.error = "boot-confirmation-failed";
      policy_.Start(nowMs - OtaPolicy::BootDeadlineMs, true);
    }
  }
  if (rollback) {
    esp_ota_mark_app_invalid_rollback_and_reboot();
    // No fallback silently confirms a broken image when rollback has no slot.
    Lock lock(mutex_);
    status_.error = "rollback-unavailable";
  }
  if (notify) xTaskNotifyGive(task_);
  if (reboot) esp_restart();
}

void EspIdfFirmwareUpdate::WorkerEntry(void* owner) { static_cast<EspIdfFirmwareUpdate*>(owner)->Worker(); }
void EspIdfFirmwareUpdate::Worker() {
  for (;;) {
    ulTaskNotifyTake(pdTRUE, portMAX_DELAY);
    Work work;
    { Lock lock(mutex_); work = work_; }
    if (work == Work::Check) Check();
    if (work == Work::Install) Install();
    { Lock lock(mutex_); work_ = Work::None; }
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

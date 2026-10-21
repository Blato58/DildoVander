#pragma once
#include <algorithm>
#include <atomic>
#include <cassert>
#include <condition_variable>
#include <functional>
#include <memory>
#include <mutex>
#include <cstdint>
#include <cstring>
#include <map>
#include <string>
#include <vector>

using esp_err_t = int;
constexpr int ESP_OK = 0, ESP_FAIL = -1, ESP_ERR_NVS_NOT_FOUND = 0x1102, pdPASS = 1;
constexpr int NVS_READONLY = 0, NVS_READWRITE = 1;
constexpr int portMAX_DELAY = -1, pdTRUE = 1;
using SemaphoreHandle_t = void*;
using TaskHandle_t = void*;
using nvs_handle_t = unsigned;
using esp_ota_handle_t = unsigned;
enum esp_ota_img_states_t { ESP_OTA_IMG_UNDEFINED, ESP_OTA_IMG_PENDING_VERIFY, ESP_OTA_IMG_VALID, ESP_OTA_IMG_INVALID, ESP_OTA_IMG_ABORTED };
struct esp_partition_t { uint32_t address; uint32_t size; };
struct esp_app_desc_t { char version[32]; char project_name[32]; uint8_t app_elf_sha256[32]; };
struct FakeHttp { std::string body; int status = 200; int64_t length = 0; size_t offset = 0; bool chunked = false; bool interrupted = false; };
using esp_http_client_handle_t = FakeHttp*;
struct esp_http_client_config_t {
  const char* url = nullptr;
  int (*crt_bundle_attach)(void*) = nullptr;
  bool disable_auto_redirect = false;
  bool skip_cert_common_name_check = false;
  int timeout_ms = 0, max_authorization_retries = 0, buffer_size = 0;
};
struct psa_key_attributes_t { unsigned type = 1, bits = 256; };
struct psa_hash_operation_t { uint8_t checksum = 0; };
struct mbedtls_pk_context { bool parsed = false; };
constexpr int PSA_SUCCESS = 0, PSA_ALG_SHA_256 = 1, PSA_KEY_USAGE_VERIFY_HASH = 1, PSA_ECC_FAMILY_SECP_R1 = 1, MBEDTLS_MD_SHA256 = 1;
#define PSA_KEY_ATTRIBUTES_INIT {}
#define PSA_HASH_OPERATION_INIT {}
inline unsigned PSA_KEY_TYPE_ECC_PUBLIC_KEY(unsigned) { return 1; }

namespace ota_fake {
extern std::atomic<uint64_t> nowMs;
extern std::map<std::string, std::string> settings;
extern std::map<std::string, FakeHttp> responses;
extern std::vector<std::string> requested;
extern std::vector<uint8_t> staged;
extern esp_partition_t running, target;
extern esp_ota_img_states_t runningState, targetState;
extern esp_app_desc_t runningDescription, stagedDescription;
extern bool signatureValid, imageValid, bootPromoted, rebooted, rolledBack, aborted;
extern unsigned writes;
extern unsigned begins;
extern size_t partitionBytesRead;
extern const void* downloadBuffer;
extern bool reusedTransferBuffer;
extern bool failMutex, failTask, confirmSucceeds, commitSucceeds;
extern unsigned taskCreates, confirms, rollbacks, commits;
extern thread_local bool onWorker;
extern std::function<void()> confirmHook, commitHook, rollbackHook, restartHook;
extern std::map<std::string, std::string> pendingSettings;
extern std::map<std::string, std::vector<uint8_t>> blobs, pendingBlobs;
extern std::vector<std::string> pendingErases;
extern bool guardReadFails, guardSecondReadFails, guardWriteFails, guardEraseFails;
extern bool guardCommitFails, guardEraseCommitFails, guardWriteLostAck, guardEraseLostAck;
extern unsigned guardReads, guardWrites, guardErases;
extern std::function<void()> guardReadHook, guardPrepareHook, guardClearHook;
extern std::vector<std::unique_ptr<std::mutex>> mutexes;
extern std::mutex taskMutex;
extern std::condition_variable taskChanged;
extern void (*taskEntry)(void*);
extern void* taskContext;
extern unsigned notifications;
extern bool stopTask;
struct StopWorker {};
void Reset();
}
inline SemaphoreHandle_t xSemaphoreCreateMutex() {
  if (ota_fake::failMutex) return nullptr;
  ota_fake::mutexes.emplace_back(new std::mutex());
  return ota_fake::mutexes.back().get();
}
inline void xSemaphoreTake(SemaphoreHandle_t handle, int) { static_cast<std::mutex*>(handle)->lock(); }
inline void xSemaphoreGive(SemaphoreHandle_t handle) { static_cast<std::mutex*>(handle)->unlock(); }
inline unsigned& LastCreatedTaskStackBytes() { static unsigned bytes = 0; return bytes; }
inline int xTaskCreate(void (*entry)(void*), const char*, unsigned stackBytes, void* context, unsigned, TaskHandle_t* handle) {
  ++ota_fake::taskCreates;
  LastCreatedTaskStackBytes() = stackBytes;
  if (ota_fake::failTask) return 0;
  ota_fake::taskEntry = entry;
  ota_fake::taskContext = context;
  *handle = reinterpret_cast<void*>(1); return pdPASS;
}
inline void xTaskNotifyGive(TaskHandle_t) {
  { std::lock_guard<std::mutex> lock(ota_fake::taskMutex); ++ota_fake::notifications; }
  ota_fake::taskChanged.notify_all();
}
inline void ulTaskNotifyTake(int, int) {
  std::unique_lock<std::mutex> lock(ota_fake::taskMutex);
  ota_fake::taskChanged.wait(lock, []() { return ota_fake::stopTask || ota_fake::notifications != 0; });
  if (ota_fake::stopTask) throw ota_fake::StopWorker();
  ota_fake::notifications = 0;
}
inline void vTaskDelay(unsigned ticks) { ota_fake::nowMs += ticks; }
inline int64_t esp_timer_get_time() { return static_cast<int64_t>(ota_fake::nowMs.load() * 1000); }
inline int nvs_open(const char*, int, nvs_handle_t* handle) { *handle = 1; return ESP_OK; }
inline void nvs_close(nvs_handle_t) {
  ota_fake::pendingSettings.clear(); ota_fake::pendingBlobs.clear(); ota_fake::pendingErases.clear();
}
inline int nvs_get_blob(nvs_handle_t, const char* key, void* bytes, size_t* length) {
  ++ota_fake::guardReads;
  if (ota_fake::guardReadHook) ota_fake::guardReadHook();
  if (ota_fake::guardReadFails || (bytes && ota_fake::guardSecondReadFails)) return ESP_FAIL;
  const auto found = ota_fake::blobs.find(key);
  if (found == ota_fake::blobs.end()) return ESP_ERR_NVS_NOT_FOUND;
  if (bytes && *length < found->second.size()) return ESP_FAIL;
  if (bytes) std::memcpy(bytes, found->second.data(), found->second.size());
  *length = found->second.size(); return ESP_OK;
}
inline int nvs_set_blob(nvs_handle_t, const char* key, const void* bytes, size_t length) {
  assert(ota_fake::onWorker); ++ota_fake::guardWrites;
  if (ota_fake::guardWriteFails) return ESP_FAIL;
  const auto* data = static_cast<const uint8_t*>(bytes);
  ota_fake::pendingBlobs[key] = std::vector<uint8_t>(data, data + length);
  return ESP_OK;
}
inline int nvs_erase_key(nvs_handle_t, const char* key) {
  assert(ota_fake::onWorker); ++ota_fake::guardErases;
  if (ota_fake::guardEraseFails) return ESP_FAIL;
  if (ota_fake::blobs.count(key) == 0) return ESP_ERR_NVS_NOT_FOUND;
  ota_fake::pendingErases.emplace_back(key); return ESP_OK;
}
inline int nvs_get_str(nvs_handle_t, const char* key, char* bytes, size_t* size) {
  if (std::strlen(key) > 15) return ESP_FAIL;
  auto found = ota_fake::settings.find(key);
  if (found == ota_fake::settings.end()) return ESP_FAIL;
  const size_t length = found->second.size() + 1;
  if (bytes && *size < length) return ESP_FAIL;
  if (bytes) std::memcpy(bytes, found->second.c_str(), length);
  *size = length;
  return ESP_OK;
}
inline int nvs_get_u8(nvs_handle_t, const char* key, uint8_t* value) {
  if (std::strlen(key) > 15) return ESP_FAIL;
  auto found = ota_fake::settings.find(key);
  if (found == ota_fake::settings.end()) return ESP_FAIL;
  *value = found->second == "1" ? 1 : 0;
  return ESP_OK;
}
inline int nvs_set_u8(nvs_handle_t, const char* key, uint8_t value) { assert(ota_fake::onWorker); if (std::strlen(key) > 15) return ESP_FAIL; ota_fake::pendingSettings[key] = value ? "1" : "0"; return ESP_OK; }
inline int nvs_commit(nvs_handle_t) {
  assert(ota_fake::onWorker);
  ++ota_fake::commits;
  if (!ota_fake::pendingBlobs.empty() || !ota_fake::pendingErases.empty()) {
    const bool erase = !ota_fake::pendingErases.empty();
    const auto& hook = erase ? ota_fake::guardClearHook : ota_fake::guardPrepareHook;
    if (hook) hook();
    if (erase ? ota_fake::guardEraseCommitFails : ota_fake::guardCommitFails) return ESP_FAIL;
    for (const auto& value : ota_fake::pendingBlobs) ota_fake::blobs[value.first] = value.second;
    for (const auto& key : ota_fake::pendingErases) ota_fake::blobs.erase(key);
    return (erase ? ota_fake::guardEraseLostAck : ota_fake::guardWriteLostAck) ? ESP_FAIL : ESP_OK;
  }
  if (ota_fake::commitHook) ota_fake::commitHook();
  if (!ota_fake::commitSucceeds) { ota_fake::pendingSettings.clear(); return ESP_FAIL; }
  for (const auto& value : ota_fake::pendingSettings) ota_fake::settings[value.first] = value.second;
  ota_fake::pendingSettings.clear();
  return ESP_OK;
}
inline int nvs_get_u32(nvs_handle_t, const char* key, uint32_t* value) {
  if (std::strlen(key) > 15) return ESP_FAIL;
  auto found = ota_fake::settings.find(key);
  if (found == ota_fake::settings.end()) return ESP_FAIL;
  *value = static_cast<uint32_t>(std::stoul(found->second));
  return ESP_OK;
}
inline int nvs_set_u32(nvs_handle_t, const char* key, uint32_t value) { if (std::strlen(key) > 15) return ESP_FAIL; assert(ota_fake::onWorker); ota_fake::pendingSettings[key] = std::to_string(value); return ESP_OK; }
inline int esp_crt_bundle_attach(void*) { return 0; }
inline esp_http_client_handle_t esp_http_client_init(const esp_http_client_config_t* config) {
  if (!config->crt_bundle_attach || !config->disable_auto_redirect || config->skip_cert_common_name_check || config->timeout_ms != 5000) return nullptr;
  ota_fake::requested.push_back(config->url);
  auto response = ota_fake::responses.find(config->url);
  return response == ota_fake::responses.end() ? nullptr : new FakeHttp(response->second);
}
inline int esp_http_client_open(esp_http_client_handle_t, int) { return ESP_OK; }
inline int64_t esp_http_client_fetch_headers(esp_http_client_handle_t client) { return client->length; }
inline int esp_http_client_get_status_code(esp_http_client_handle_t client) { return client->status; }
inline bool esp_http_client_is_chunked_response(esp_http_client_handle_t client) { return client->chunked; }
inline int esp_http_client_read(esp_http_client_handle_t client, char* buffer, int count) {
  ota_fake::downloadBuffer = buffer;
  if (client->interrupted && client->offset >= 1024) return -1;
  size_t available = std::min(static_cast<size_t>(count), client->body.size() - client->offset);
  if (client->interrupted) available = std::min(available, static_cast<size_t>(1024));
  std::memcpy(buffer, client->body.data() + client->offset, available);
  client->offset += available;
  return static_cast<int>(available);
}
inline bool esp_http_client_is_complete_data_received(esp_http_client_handle_t client) { return client->offset == static_cast<size_t>(client->length); }
inline void esp_http_client_cleanup(esp_http_client_handle_t client) { delete client; }
inline const esp_partition_t* esp_ota_get_running_partition() { return &ota_fake::running; }
inline const esp_partition_t* esp_ota_get_next_update_partition(const void*) { return &ota_fake::target; }
inline int esp_ota_get_state_partition(const esp_partition_t* partition, esp_ota_img_states_t* state) { *state = partition == &ota_fake::running ? ota_fake::runningState : ota_fake::targetState; return ESP_OK; }
inline int esp_ota_mark_app_valid_cancel_rollback() {
  assert(ota_fake::onWorker); ++ota_fake::confirms;
  if (ota_fake::confirmHook) ota_fake::confirmHook();
  if (!ota_fake::confirmSucceeds) return ESP_FAIL;
  ota_fake::runningState = ESP_OTA_IMG_VALID; return ESP_OK;
}
inline int esp_ota_mark_app_invalid_rollback_and_reboot() {
  assert(ota_fake::onWorker); ++ota_fake::rollbacks;
  if (ota_fake::rollbackHook) ota_fake::rollbackHook();
  ota_fake::rolledBack = true; return ESP_FAIL; // No reboot in the host process.
}
inline int esp_ota_begin(const esp_partition_t*, size_t, esp_ota_handle_t* handle) { ++ota_fake::begins; *handle = 1; ota_fake::staged.clear(); return ESP_OK; }
inline int esp_ota_write(esp_ota_handle_t, const void* bytes, size_t length) {
  const auto* buffer = static_cast<const uint8_t*>(bytes);
  ota_fake::staged.insert(ota_fake::staged.end(), buffer, buffer + length);
  ++ota_fake::writes;
  return ESP_OK;
}
inline int esp_ota_end(esp_ota_handle_t) { return ota_fake::imageValid ? ESP_OK : ESP_FAIL; }
inline void esp_ota_abort(esp_ota_handle_t) { ota_fake::aborted = true; }
inline int esp_ota_set_boot_partition(const esp_partition_t*) { ota_fake::bootPromoted = true; return ESP_OK; }
inline int esp_partition_read(const esp_partition_t*, size_t offset, void* buffer, size_t length) {
  if (offset + length > ota_fake::staged.size()) return ESP_FAIL;
  ota_fake::partitionBytesRead += length;
  ota_fake::reusedTransferBuffer = ota_fake::reusedTransferBuffer && buffer == ota_fake::downloadBuffer;
  std::memcpy(buffer, ota_fake::staged.data() + offset, length);
  return ESP_OK;
}
inline int esp_ota_get_partition_description(const esp_partition_t* partition, esp_app_desc_t* description) { *description = partition == &ota_fake::running ? ota_fake::runningDescription : ota_fake::stagedDescription; return ESP_OK; }
inline const esp_app_desc_t* esp_app_get_description() { return &ota_fake::runningDescription; }
inline void esp_restart() { assert(ota_fake::onWorker); if (ota_fake::restartHook) ota_fake::restartHook(); ota_fake::rebooted = true; }
inline int psa_crypto_init() { return PSA_SUCCESS; }
inline void mbedtls_pk_init(mbedtls_pk_context* key) { key->parsed = false; }
inline void mbedtls_pk_free(mbedtls_pk_context*) {}
inline int mbedtls_pk_parse_public_key(mbedtls_pk_context* key, const uint8_t*, size_t) { key->parsed = true; return 0; }
inline int mbedtls_pk_get_psa_attributes(const mbedtls_pk_context*, int, psa_key_attributes_t*) { return 0; }
inline unsigned psa_get_key_type(const psa_key_attributes_t* attributes) { return attributes->type; }
inline unsigned psa_get_key_bits(const psa_key_attributes_t* attributes) { return attributes->bits; }
inline void psa_reset_key_attributes(psa_key_attributes_t*) {}
inline int mbedtls_pk_verify(mbedtls_pk_context*, int, const uint8_t*, size_t, const uint8_t*, size_t) { return ota_fake::signatureValid ? 0 : -1; }
inline int psa_hash_compute(int, const uint8_t*, size_t, uint8_t* hash, size_t size, size_t* length) { std::memset(hash, 0, size); *length = size; return 0; }
inline int psa_hash_setup(psa_hash_operation_t* hash, int) { hash->checksum = 0; return 0; }
inline int psa_hash_update(psa_hash_operation_t* hash, const uint8_t* bytes, size_t length) { for (size_t i = 0; i < length; ++i) hash->checksum ^= bytes[i]; return 0; }
inline int psa_hash_finish(psa_hash_operation_t* operation, uint8_t* hash, size_t size, size_t* length) { std::memset(hash, 0, size); hash[0] = operation->checksum; *length = size; return 0; }
inline void psa_hash_abort(psa_hash_operation_t*) {}

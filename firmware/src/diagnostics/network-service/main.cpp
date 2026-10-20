#include <array>
#include <cstring>
#include <cstdlib>
#include <cstdio>
#include <memory>
#include <new>
#include "cJSON.h"
#include "driver/usb_serial_jtag.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "esp_timer.h"
#include "nvs.h"
#include "nvs_flash.h"
#include "mbedtls/pk.h"
#include "psa/crypto.h"
#include "NetworkSettings.h"
#include "DeviceIdentity.h"
#include "EspIdfProvisioningStorage.h"
#include "features/device_control/EspIdfJournalStorage.h"
#include "features/device_control/EspIdfJournalIdentityStorage.h"
#include "features/device_control/JournalServiceEstablishment.h"
#include "features/keepsake/EspIdfKeepsakeInventoryStorage.h"
#include "features/keepsake/KeepsakeCommittedInventory.h"
#include "features/offline_show/EspIdfOfflineOutboxStorage.h"

namespace {
constexpr size_t MaximumFrame = 4096;
void Wipe(void* memory, size_t length) {
  auto* bytes = static_cast<volatile uint8_t*>(memory);
  for (size_t index = 0; index < length; ++index) { bytes[index] = 0; }
}
const char* Text(cJSON* object, const char* name) {
  const auto* field = cJSON_GetObjectItemCaseSensitive(object, name);
  return cJSON_IsString(field) ? field->valuestring : nullptr;
}
bool Hex(const char* text, size_t length) {
  if (text == nullptr || std::strlen(text) != length) { return false; }
  for (size_t i = 0; i < length; ++i) {
    if (!((text[i] >= '0' && text[i] <= '9') || (text[i] >= 'a' && text[i] <= 'f'))) { return false; }
  }
  return true;
}
bool PublicTrust(const char* pem) {
  if (pem == nullptr || std::strlen(pem) > 1024) { return false; }
  mbedtls_pk_context key;
  mbedtls_pk_init(&key);
  psa_key_attributes_t attributes = PSA_KEY_ATTRIBUTES_INIT;
  const bool valid = psa_crypto_init() == PSA_SUCCESS &&
      mbedtls_pk_parse_public_key(&key, reinterpret_cast<const uint8_t*>(pem), std::strlen(pem) + 1) == 0 &&
      mbedtls_pk_get_psa_attributes(&key, PSA_KEY_USAGE_VERIFY_HASH, &attributes) == 0 &&
      psa_get_key_type(&attributes) == PSA_KEY_TYPE_ECC_PUBLIC_KEY(PSA_ECC_FAMILY_SECP_R1) &&
      psa_get_key_bits(&attributes) == 256;
  psa_reset_key_attributes(&attributes);
  mbedtls_pk_free(&key);
  return valid;
}
bool Origin(const char* origin) {
  if (origin == nullptr || std::strncmp(origin, "https://", 8) != 0 || std::strlen(origin) > 267) { return false; }
  std::string host = origin + 8;
  if (host.size() > 4 && host.substr(host.size() - 4) == ":443") { host.resize(host.size() - 4); }
  return NetworkSettings::IsValidBackendHost(host);
}
class PhysicalServiceGate final : public ServiceGate {
 public:
  bool ServiceModeActive() const override { return true; }
  bool ActuatorSupplyDisconnected() const override { return true; }
};
// This diagnostic image has no producers or actuators. This barrier is local to
// its exclusive service owner; it is not the unfinished production barrier.
class ServiceBarrier final : public JournalReconciliationBarrier {
 public:
  bool TryEnter() override { if (held_) return false; held_ = true; return true; }
  void Leave() override { held_ = false; }
 private:
  bool held_ = false;
};
struct JournalServiceContext {
  JournalServiceContext(DeviceIdentity& identity, const ServiceGate& gate)
      : journalStorage(true), markerStorage(true), journal(journalStorage),
        witness(markerStorage, journal), onlineStorage(true), online(onlineStorage), offline(true),
        reconciliation(journal, witness, online, offline, barrier),
        service(identity, gate, witness, online, offline, reconciliation) {}
  EspIdfJournalStorage journalStorage;
  EspIdfJournalIdentityStorage markerStorage;
  PersistentCommandJournal journal;
  JournalIdentityOwner witness;
  EspIdfKeepsakeInventoryStorage onlineStorage;
  KeepsakeCommittedInventory online;
  kufr::offline::EspIdfOutboxStorage offline;
  ServiceBarrier barrier;
  JournalReconciliation reconciliation;
  JournalServiceEstablishment service;
};
const char* EstablishmentResult(JournalServiceEstablishment::Result result) {
  using Result = JournalServiceEstablishment::Result;
  switch (result) {
    case Result::Established: return nullptr;
    case Result::EvidenceBlocked: return "journal-evidence-blocked";
    case Result::IdentityReplacementDenied: return "journal-identity-replacement-denied";
    case Result::InventoryRejected: return "journal-inventory-rejected";
    case Result::MarkerRejected: return "journal-marker-rejected";
    case Result::JournalRejected: return "journal-state-rejected";
    case Result::ServiceDenied: return "journal-service-denied";
    case Result::InvalidRequest: return "journal-invalid-request";
    case Result::IdentityUnavailable: return "journal-identity-unavailable";
    default: return "journal-write-failed-reopen-required";
  }
}
const char* Apply(cJSON* request) {
  const char* confirmation = Text(request, "physicalConfirmation");
  const char* proof = Text(request, "pop");
  const char* backend = Text(request, "backendHost");
  if (!cJSON_IsObject(request) || confirmation == nullptr ||
      std::strcmp(confirmation, "ACTUATORS_OFF_USB_SERVICE") != 0 || !Hex(proof, 32) ||
      (backend != nullptr && !NetworkSettings::IsValidBackendHost(backend))) { return "invalid-request"; }
  const char* deviceId = Text(request, "deviceId");
  const char* credential = Text(request, "credential");
  const char* journalMode = Text(request, "journalEstablishment");
  if (cJSON_GetObjectItemCaseSensitive(request, "journalEstablishment") &&
      (!journalMode || (std::strcmp(journalMode, "factory") != 0 &&
       std::strcmp(journalMode, "legacy") != 0 && std::strcmp(journalMode, "resume") != 0)))
    return "invalid-journal-establishment";
  if (journalMode && cJSON_IsTrue(cJSON_GetObjectItemCaseSensitive(request, "replaceIdentity")))
    return "journal-identity-replacement-denied";
  if (journalMode && ((cJSON_GetObjectItemCaseSensitive(request, "deviceId") && !deviceId) ||
      (cJSON_GetObjectItemCaseSensitive(request, "credential") && !credential))) return "invalid-identity";
  if ((deviceId == nullptr) != (credential == nullptr) ||
      (credential != nullptr && (!Hex(credential, 64) || std::strspn(credential, "0") == 64))) { return "invalid-identity"; }
  cJSON* ota = cJSON_GetObjectItemCaseSensitive(request, "ota");
  if (ota != nullptr) {
    const char* channel = Text(ota, "channel");
    const auto* interval = cJSON_GetObjectItemCaseSensitive(ota, "checkIntervalSeconds");
    if (!cJSON_IsObject(ota) || !Origin(Text(ota, "origin")) || !PublicTrust(Text(ota, "trustPem")) ||
        channel == nullptr || (std::strcmp(channel, "stable") != 0 && std::strcmp(channel, "development") != 0) ||
        !cJSON_IsBool(cJSON_GetObjectItemCaseSensitive(ota, "automaticInstall")) || !cJSON_IsNumber(interval) ||
        interval->valuedouble < 60 || interval->valuedouble > 86400 || interval->valuedouble != interval->valueint) { return "invalid-ota"; }
  }
  nvs_handle_t ble;
  if (nvs_open("wand_ble", NVS_READWRITE, &ble) != ESP_OK) { return "nvs-failed"; }
  size_t existingSize = 0;
  const auto exists = nvs_get_str(ble, "pop", nullptr, &existingSize);
  char existingProof[33]{};
  bool sameProof = false;
  if (exists == ESP_OK && existingSize == sizeof(existingProof) &&
      nvs_get_str(ble, "pop", existingProof, &existingSize) == ESP_OK) {
    uint8_t difference = 0;
    for (size_t index = 0; index < 32; ++index) { difference |= existingProof[index] ^ proof[index]; }
    sameProof = difference == 0;
  }
  Wipe(existingProof, sizeof(existingProof));
  if (exists == ESP_OK && !sameProof && !cJSON_IsTrue(cJSON_GetObjectItemCaseSensitive(request, "replacePoP"))) {
    nvs_close(ble); return "pop-exists";
  }
  if (exists != ESP_OK && exists != ESP_ERR_NVS_NOT_FOUND) { nvs_close(ble); return "nvs-failed"; }
  if (deviceId != nullptr || journalMode != nullptr) {
    if (deviceId && (std::strlen(deviceId) == 0 || std::strlen(deviceId) > 64 ||
        std::strspn(deviceId, "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_") != std::strlen(deviceId))) {
      nvs_close(ble); return "invalid-identity";
    }
    EspIdfProvisioningStorage identityStorage(true);
    DeviceIdentity identity(identityStorage);
    if (!journalMode && identity.IsProvisioned() &&
        !cJSON_IsTrue(cJSON_GetObjectItemCaseSensitive(request, "replaceIdentity"))) {
      nvs_close(ble); return "identity-exists";
    }
    std::array<uint8_t, 32> bytes{};
    for (size_t i = 0; credential && i < bytes.size(); ++i) {
      char pair[3] = {credential[i * 2], credential[i * 2 + 1], 0};
      bytes[i] = static_cast<uint8_t>(std::strtoul(pair, nullptr, 16));
    }
    PhysicalServiceGate gate;
    // Heap-owned bounded snapshots keep marker/codecs off the small app task
    // stack. Reconstruct every attempt after any uncertain durable ACK.
    std::unique_ptr<JournalServiceContext> context(new (std::nothrow) JournalServiceContext(identity, gate));
    const char* error = context ? nullptr : "service-memory-unavailable";
    if (context && journalMode) {
      using Mode = JournalServiceEstablishment::Mode;
      const Mode mode = std::strcmp(journalMode, "factory") == 0 ? Mode::Factory :
          std::strcmp(journalMode, "legacy") == 0 ? Mode::Legacy : Mode::Resume;
      const std::string target = deviceId ? deviceId : identity.IsProvisioned()
          ? identity.DeviceId() : context->witness.TargetDeviceId();
      error = EstablishmentResult(context->service.Run(mode, target, credential ? &bytes : nullptr));
    } else if (context) {
      if (!context->witness.IsHealthy() ||
          context->witness.CurrentPhase() != JournalIdentityOwner::Phase::Missing ||
          !context->journal.IsHealthy() ||
          context->journal.Origin() == PersistentCommandJournal::StateOrigin::Scoped)
        error = "journal-mode-required";
      else if (!identity.Provision(deviceId, bytes, gate)) error = "nvs-failed";
    }
    Wipe(bytes.data(), bytes.size());
    if (error) { nvs_close(ble); return error; }
  }
  if (backend != nullptr) {
    nvs_handle_t network;
    if (nvs_open("wand_network", NVS_READWRITE, &network) != ESP_OK) { nvs_close(ble); return "nvs-failed"; }
    const bool saved = nvs_set_str(network, "backend_host", backend) == ESP_OK && nvs_commit(network) == ESP_OK;
    nvs_close(network);
    if (!saved) { nvs_close(ble); return "nvs-failed"; }
  }
  if (ota != nullptr) {
    nvs_handle_t updater;
    if (nvs_open("wand_ota", NVS_READWRITE, &updater) != ESP_OK) { nvs_close(ble); return "nvs-failed"; }
    // Disable automatic installation before any update to the trust/origin tuple.
    bool saved = nvs_set_u8(updater, "auto_install", 0) == ESP_OK && nvs_commit(updater) == ESP_OK;
    saved = saved && nvs_set_str(updater, "origin", Text(ota, "origin")) == ESP_OK &&
        nvs_set_str(updater, "channel", Text(ota, "channel")) == ESP_OK &&
        nvs_set_str(updater, "trust_pem", Text(ota, "trustPem")) == ESP_OK &&
        nvs_set_u32(updater, "check_interval", cJSON_GetObjectItemCaseSensitive(ota, "checkIntervalSeconds")->valueint) == ESP_OK &&
        nvs_commit(updater) == ESP_OK;
    if (saved) {
      saved = nvs_set_u8(updater, "auto_install", cJSON_IsTrue(cJSON_GetObjectItemCaseSensitive(ota, "automaticInstall")) ? 1 : 0) == ESP_OK &&
          nvs_commit(updater) == ESP_OK;
    }
    nvs_close(updater);
    if (!saved) { nvs_close(ble); return "nvs-failed"; }
  }
  const bool saved = nvs_set_str(ble, "pop", proof) == ESP_OK && nvs_commit(ble) == ESP_OK;
  nvs_close(ble);
  return saved ? "saved" : "nvs-failed";
}
void Send(const char* text) {
  usb_serial_jtag_write_bytes(text, std::strlen(text), pdMS_TO_TICKS(1000));
}
void EraseStrings(cJSON* value) {
  if (value == nullptr) { return; }
  if (cJSON_IsString(value)) { Wipe(value->valuestring, std::strlen(value->valuestring)); }
  for (cJSON* child = value->child; child != nullptr; child = child->next) { EraseStrings(child); }
}
}
namespace {
void ServiceLoop() {
  usb_serial_jtag_driver_config_t config{};
  config.tx_buffer_size = 512;
  config.rx_buffer_size = 4096;
  if (usb_serial_jtag_driver_install(&config) != ESP_OK) { return; }
  if (nvs_flash_init() != ESP_OK) { Send("KUFR_SERVICE_RESULT nvs-failed\n"); return; }
  static char frame[MaximumFrame + 1]{};
  size_t length = 0;
  bool overflow = false;
  unsigned attempts = 0;
  const int64_t deadline = esp_timer_get_time() + INT64_C(300000000);
  int64_t readyAt = 0;
  while (esp_timer_get_time() < deadline && attempts < 5) {
    if (length == 0 && esp_timer_get_time() >= readyAt) {
      Send("KUFR_SERVICE_READY\n"); readyAt = esp_timer_get_time() + 1000000;
    }
    char byte;
    if (usb_serial_jtag_read_bytes(&byte, 1, pdMS_TO_TICKS(100)) != 1) { continue; }
    if (byte != '\n') {
      if (byte == 0 || length == MaximumFrame) { overflow = true; }
      else if (!overflow) { frame[length++] = byte; }
      continue;
    }
    ++attempts;
    frame[length] = 0;
    const char* result = "invalid-request";
    // Flat service schema only needs two object levels. Bound parser recursion.
    unsigned depth = 0;
    bool quoted = false, escaped = false, bounded = true;
    for (size_t i = 0; i < length; ++i) {
      const char value = frame[i];
      if (quoted) {
        if (escaped) { escaped = false; }
        else if (value == '\\') { escaped = true; }
        else if (value == '"') { quoted = false; }
      } else if (value == '"') { quoted = true; }
      else if (value == '{' || value == '[') { if (++depth > 2) { bounded = false; break; } }
      else if (value == '}' || value == ']') { if (depth == 0) { bounded = false; break; } --depth; }
    }
    cJSON* request = !overflow && bounded && depth == 0 && !quoted && std::strstr(frame, "\\u0000") == nullptr ?
        cJSON_ParseWithOpts(frame, nullptr, true) : nullptr;
    if (request != nullptr) { result = Apply(request); }
    EraseStrings(request); cJSON_Delete(request); Wipe(frame, sizeof(frame));
    Send("KUFR_SERVICE_RESULT "); Send(result); Send("\n");
    if (std::strcmp(result, "saved") == 0) { return; }
    length = 0; overflow = false;
  }
  Wipe(frame, sizeof(frame));
  Send("KUFR_SERVICE_RESULT window-closed\n");
}
}
extern "C" void app_main() {
  // Canonical marker decode nests real journal validation. Board high-water
  // acceptance is still required; the small default app_main stack is avoided.
  constexpr uint32_t ServiceStackBytes = 32768;
  const auto task = [](void*) {
    ServiceLoop();
    char report[80];
    std::snprintf(report, sizeof(report), "KUFR_SERVICE_STACK_FREE %u\n",
        static_cast<unsigned>(uxTaskGetStackHighWaterMark(nullptr)));
    Send(report);
    vTaskDelete(nullptr);
  };
  if (xTaskCreate(task, "journal-service", ServiceStackBytes, nullptr, 5, nullptr) != pdPASS)
    Send("KUFR_SERVICE_RESULT service-memory-unavailable\n");
}

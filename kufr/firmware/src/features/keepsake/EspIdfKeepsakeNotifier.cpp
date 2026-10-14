#include "EspIdfKeepsakeNotifier.h"

#include <array>
#include <cmath>
#include <ctime>
#include <cstring>
#include "cJSON.h"
#include "esp_crt_bundle.h"
#include "esp_http_client.h"
#include "esp_timer.h"
#include "nvs.h"
#include "../backend_access/DeviceTokenSession.h"
#include "../backend_access/EspIdfStationNetwork.h"
#include "../backend_access/EspIdfTokenExchangeTransport.h"
#include "../conversation/ConversationStartCodec.h"
#include "../conversation/TurnPollGuard.h"
#include "../device_control/PersistentCommandJournal.h"

namespace {
uint64_t NowMs() { return static_cast<uint64_t>(esp_timer_get_time() / 1000); }

bool Valid(const KeepsakeOutcome& item) {
  return IsConversationUuid(item.operationId) && IsConversationUuid(item.outcomeId)
      && IsConversationUuid(item.proposalId) && IsConversationUuid(item.physicalOperationId)
      && IsConversationUuid(item.bootId) && item.generation <= INT32_MAX && item.deviceRevision <= INT32_MAX;
}

std::string Body(const KeepsakeOutcome& item, bool persistent) {
  return "{\"schemaVersion\":1," + std::string(persistent ? "\"operationId\":\"" + item.operationId + "\"," : "")
      + "\"outcomeId\":\"" + item.outcomeId + "\",\"proposalId\":\"" + item.proposalId
      + "\",\"physicalOperationId\":\"" + item.physicalOperationId + "\",\"bootId\":\"" + item.bootId
      + "\",\"generation\":" + std::to_string(item.generation) + ",\"deviceRevision\":"
      + std::to_string(item.deviceRevision) + (persistent ? ",\"compartment\":" + std::to_string(item.compartment) : "")
      + ",\"presentationEvidence\":\"completed_by_timing\"}";
}

bool Text(cJSON* root, const char* name, std::string& target, size_t maximum = 36) {
  auto* value = cJSON_GetObjectItemCaseSensitive(root, name);
  if (!cJSON_IsString(value) || !value->valuestring || std::strlen(value->valuestring) > maximum) return false;
  target = value->valuestring;
  return true;
}

bool Number(cJSON* root, const char* name, uint64_t& target) {
  auto* value = cJSON_GetObjectItemCaseSensitive(root, name);
  if (!cJSON_IsNumber(value) || !std::isfinite(value->valuedouble) || value->valuedouble < 0
      || value->valuedouble > INT32_MAX || std::floor(value->valuedouble) != value->valuedouble) return false;
  target = static_cast<uint64_t>(value->valuedouble);
  return true;
}

struct Response {
  std::array<char, 2049> bytes{};
  size_t length = 0;
  bool overflow = false;
  ~Response() { std::fill(bytes.begin(), bytes.end(), '\0'); }
};

std::string ErrorCode(const Response& response) {
  auto* root = cJSON_ParseWithLengthOpts(response.bytes.data(), response.length + 1, nullptr, true);
  std::string code;
  if (!cJSON_IsObject(root) || cJSON_GetArraySize(root) != 1 || !Text(root, "errorCode", code, 64)) code.clear();
  cJSON_Delete(root);
  return code;
}

esp_err_t Event(esp_http_client_event_t* event) {
  if (event->event_id != HTTP_EVENT_ON_DATA || event->data_len <= 0) return ESP_OK;
  auto& response = *static_cast<Response*>(event->user_data);
  if (static_cast<size_t>(event->data_len) > response.bytes.size() - 1 - response.length) {
    response.overflow = true;
    return ESP_FAIL;
  }
  std::memcpy(response.bytes.data() + response.length, event->data, event->data_len);
  response.length += event->data_len;
  return ESP_OK;
}

int Post(const std::string& host, const std::string& path, const std::string& body,
         const std::string& key, const char* token, Response& response) {
  if (!token || !NetworkSettings::IsValidBackendHost(host)) return 0;
  const std::string url = "https://" + host + path;
  esp_http_client_config_t config{};
  config.url = url.c_str();
  config.crt_bundle_attach = esp_crt_bundle_attach;
  config.disable_auto_redirect = true;
  config.max_authorization_retries = -1;
  config.timeout_ms = 5000;
  config.event_handler = Event;
  config.user_data = &response;
  auto client = esp_http_client_init(&config);
  if (!client) return 0;
  std::string authorization = "Bearer " + std::string(token);
  esp_http_client_set_method(client, HTTP_METHOD_POST);
  esp_http_client_set_header(client, "Authorization", authorization.c_str());
  esp_http_client_set_header(client, "Content-Type", "application/json");
  esp_http_client_set_header(client, "Idempotency-Key", key.c_str());
  esp_http_client_set_post_field(client, body.data(), body.size());
  const auto error = esp_http_client_perform(client);
  const int status = esp_http_client_get_status_code(client);
  const bool complete = esp_http_client_is_complete_data_received(client);
  esp_http_client_cleanup(client);
  std::fill(authorization.begin(), authorization.end(), '\0');
  return !response.overflow && (status == 401 || (error == ESP_OK && complete)) ? status : 0;
}

bool SafeClaimUrl(const std::string& url, const std::string& host) {
  const std::string prefix = "https://" + host + "/keepsake/#c=";
  if (url.compare(0, prefix.size(), prefix) != 0 || url.size() < prefix.size() + 32 || url.size() > prefix.size() + 128) return false;
  for (size_t i = prefix.size(); i < url.size(); ++i) {
    const char c = url[i];
    if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_')) return false;
  }
  return true;
}
}  // namespace

EspIdfKeepsakeNotifier::EspIdfKeepsakeNotifier(DeviceIdentity& identity, EspIdfStationNetwork& station,
                                           const std::atomic<bool>& backendOnline,
                                           const CommandJournal& ownerJournal, JournalStorage& storage)
    : identity_(identity), station_(station), backendOnline_(backendOnline),
      ownerJournal_(ownerJournal), journalStorage_(storage) {}

bool EspIdfKeepsakeNotifier::Start(bool initialized) {
  if (task_ || !initialized || !Load()) return false;
  // Boot-only persisted snapshot, before the physical control owner runs.
  PersistentCommandJournal savedJournal(journalStorage_);
  if (!outbox_.RecoverPrepared(savedJournal) || !PersistCurrent()) return false;
  healthy_.store(true);
  if (xTaskCreate(TaskEntry, "keepsake_outbox", 8192, this, 2, &task_) != pdPASS) {
    task_ = nullptr;
    healthy_.store(false);
    return false;
  }
  return true;
}

bool EspIdfKeepsakeNotifier::Prepare(const KeepsakeOutcome& outcome) {
  if (!healthy_.load() || !Valid(outcome) || outcome.compartment < 1 || outcome.compartment > 6) return false;
  std::lock_guard<std::mutex> lock(persistenceMutex_);
  if (!outbox_.Prepare(outcome)) return false;
  if (Persist(outbox_.State())) return true;
  outbox_.RemovePrepared(outcome.outcomeId);
  healthy_.store(false);
  return false;
}

KeepsakeQueueResult EspIdfKeepsakeNotifier::Queue(const KeepsakeOutcome& outcome) {
  if (!healthy_.load() || !Valid(outcome)
      || !ownerJournal_.WasTimedPresentationCompleted(outcome.physicalOperationId, outcome.compartment))
    return KeepsakeQueueResult::Rejected;
  const auto result = outbox_.Queue(outcome);
  if (result == KeepsakeQueueResult::VolatilePendingPersistence && task_) xTaskNotifyGive(task_);
  return result;
}

LocalKeepsakeStatus EspIdfKeepsakeNotifier::Status(uint64_t generation) const {
  const auto utc = std::time(nullptr);
  return utc > 0 ? outbox_.Read(generation, static_cast<uint64_t>(utc) * 1000) : LocalKeepsakeStatus{};
}

bool EspIdfKeepsakeNotifier::Load() {
  nvs_handle_t nvs;
  if (nvs_open("dv10_outbox", NVS_READWRITE, &nvs) != ESP_OK) return false;
  size_t length = 0;
  const auto result = nvs_get_blob(nvs, "completed", nullptr, &length);
  if (result == ESP_ERR_NVS_NOT_FOUND) { nvs_close(nvs); return true; }
  std::vector<char> bytes(16385, '\0');
  const bool loaded = result == ESP_OK && length > 0 && length < bytes.size()
      && nvs_get_blob(nvs, "completed", bytes.data(), &length) == ESP_OK;
  nvs_close(nvs);
  if (!loaded) return false;
  auto* root = cJSON_ParseWithLength(bytes.data(), length);
  auto* pending = cJSON_IsArray(root) ? root : cJSON_GetObjectItemCaseSensitive(root, "pending");
  auto* quarantined = cJSON_GetObjectItemCaseSensitive(root, "quarantined");
  auto* prepared = cJSON_GetObjectItemCaseSensitive(root, "prepared");
  std::vector<KeepsakeOutcome> outcomes;
  std::vector<KeepsakeDisposition> dispositions;
  std::vector<KeepsakeOutcome> preparations;
  uint64_t version = 0;
  bool valid = (cJSON_IsArray(root) || (Number(root, "schemaVersion", version) && (version == 1 || version == 2) && cJSON_IsArray(quarantined)))
      && cJSON_IsArray(pending) && cJSON_GetArraySize(pending) <= static_cast<int>(KeepsakeOutbox::Capacity);
  for (int i = 0; valid && i < cJSON_GetArraySize(pending); ++i) {
    auto* entry = cJSON_GetArrayItem(pending, i);
    KeepsakeOutcome item;
    valid = Text(entry, "operationId", item.operationId) && Text(entry, "outcomeId", item.outcomeId)
        && Text(entry, "proposalId", item.proposalId) && Text(entry, "physicalOperationId", item.physicalOperationId)
        && Text(entry, "bootId", item.bootId) && Number(entry, "generation", item.generation)
        && Number(entry, "deviceRevision", item.deviceRevision) && Valid(item);
    uint64_t compartment = 0;
    if (cJSON_GetObjectItemCaseSensitive(entry, "compartment")) {
      valid = valid && Number(entry, "compartment", compartment) && compartment <= 6;
      item.compartment = static_cast<uint8_t>(compartment);
    }
    if (valid) outcomes.push_back(std::move(item));
  }
  if (quarantined && cJSON_GetArraySize(quarantined) > static_cast<int>(KeepsakeOutbox::QuarantineCapacity)) valid = false;
  for (int i = 0; valid && quarantined && i < cJSON_GetArraySize(quarantined); ++i) {
    auto* entry = cJSON_GetArrayItem(quarantined, i);
    KeepsakeDisposition disposition;
    valid = Text(entry, "operationId", disposition.operationId) && IsConversationUuid(disposition.operationId)
        && Text(entry, "outcomeId", disposition.outcomeId) && IsConversationUuid(disposition.outcomeId)
        && Text(entry, "reason", disposition.reason, 64)
        && (PermanentKeepsakeFailure(410, disposition.reason) || PermanentKeepsakeFailure(409, disposition.reason)
            || PermanentKeepsakeFailure(404, disposition.reason) || disposition.reason == "physical_outcome_unknown");
    if (valid) dispositions.push_back(std::move(disposition));
  }
  if (prepared && (!cJSON_IsArray(prepared) || cJSON_GetArraySize(prepared) > static_cast<int>(KeepsakeOutbox::Capacity))) valid = false;
  if (version == 2 && !prepared) valid = false;
  for (int i = 0; valid && prepared && i < cJSON_GetArraySize(prepared); ++i) {
    auto* entry = cJSON_GetArrayItem(prepared, i);
    KeepsakeOutcome item;
    uint64_t compartment = 0;
    valid = Text(entry, "operationId", item.operationId) && Text(entry, "outcomeId", item.outcomeId)
        && Text(entry, "proposalId", item.proposalId) && Text(entry, "physicalOperationId", item.physicalOperationId)
        && Text(entry, "bootId", item.bootId) && Number(entry, "generation", item.generation)
        && Number(entry, "deviceRevision", item.deviceRevision) && Number(entry, "compartment", compartment)
        && compartment >= 1 && compartment <= 6 && Valid(item);
    item.compartment = static_cast<uint8_t>(compartment);
    if (valid) preparations.push_back(std::move(item));
  }
  cJSON_Delete(root);
  if (!valid || !outbox_.Restore(std::move(outcomes), std::move(dispositions), std::move(preparations))) return false;
  persisted_ = bytes.data();
  return true;
}

bool EspIdfKeepsakeNotifier::PersistCurrent() {
  std::lock_guard<std::mutex> lock(persistenceMutex_);
  return Persist(outbox_.State());
}

bool EspIdfKeepsakeNotifier::Persist(const KeepsakeOutboxState& state) {
  std::string bytes = "{\"schemaVersion\":2,\"pending\":[";
  for (size_t i = 0; i < state.pending.size(); ++i) {
    if (i) bytes += ',';
    bytes += Body(state.pending[i], true);
  }
  bytes += "],\"quarantined\":[";
  for (size_t i = 0; i < state.quarantined.size(); ++i) {
    if (i) bytes += ',';
    const auto& item = state.quarantined[i];
    bytes += "{\"operationId\":\"" + item.operationId + "\",\"outcomeId\":\"" + item.outcomeId
        + "\",\"reason\":\"" + item.reason + "\"}";
  }
  bytes += "],\"prepared\":[";
  for (size_t i = 0; i < state.prepared.size(); ++i) {
    if (i) bytes += ',';
    bytes += Body(state.prepared[i], true);
  }
  bytes += "]}";
  if (bytes.size() >= 16384) return false;
  if (bytes == persisted_) return true;
  nvs_handle_t nvs;
  if (nvs_open("dv10_outbox", NVS_READWRITE, &nvs) != ESP_OK) return false;
  const bool stored = nvs_set_blob(nvs, "completed", bytes.c_str(), bytes.size() + 1) == ESP_OK && nvs_commit(nvs) == ESP_OK;
  nvs_close(nvs);
  if (stored) persisted_ = std::move(bytes);
  return stored;
}

bool EspIdfKeepsakeNotifier::Deliver(const KeepsakeOutcome& outcome, const char* token, int& status, std::string& errorCode) {
  errorCode.clear();
  const std::string host = station_.Settings().BackendHost();
  Response received;
  status = Post(host, "/v1/keepsakes/outcomes/" + outcome.operationId, Body(outcome, false), outcome.outcomeId, token, received);
  if (status != 201 && status != 200) { errorCode = ErrorCode(received); return false; }
  auto* root = cJSON_ParseWithLength(received.bytes.data(), received.length);
  std::string locator, resultOutcome, physical;
  uint64_t epoch = 0;
  const bool matching = Text(root, "locator", locator) && IsConversationUuid(locator)
      && Text(root, "outcomeId", resultOutcome) && resultOutcome == outcome.outcomeId
      && Text(root, "physicalOperationId", physical) && physical == outcome.physicalOperationId
      && Number(root, "sessionEpoch", epoch) && epoch == outcome.generation;
  cJSON_Delete(root);
  if (!matching) return false;
  Response claim;
  status = Post(host, "/v1/keepsakes/" + locator + "/claim", "{}", outcome.outcomeId, token, claim);
  if (status != 200) { errorCode = ErrorCode(claim); return false; }
  root = cJSON_ParseWithLength(claim.bytes.data(), claim.length);
  LocalKeepsakeStatus visible;
  visible.outcomeId = outcome.outcomeId;
  visible.sessionEpoch = outcome.generation;
  visible.physicalOperationId = outcome.physicalOperationId;
  std::string expires, claimLocator;
  uint64_t expiry = 0;
  const bool accepted = Text(root, "locator", claimLocator) && claimLocator == locator
      && Text(root, "url", visible.url, 512) && SafeClaimUrl(visible.url, host)
      && Text(root, "expiresAt", expires, 35) && ParseConversationUtcExpiry(expires, expiry);
  cJSON_Delete(root);
  if (!accepted) return false;
  visible.expiresAtEpochMs = expiry * 1000;
  outbox_.Publish(outcome, std::move(visible));
  return true;
}

void EspIdfKeepsakeNotifier::TaskEntry(void* context) {
  static_cast<EspIdfKeepsakeNotifier*>(context)->Run();
}

void EspIdfKeepsakeNotifier::Run() {
  EspIdfTokenExchangeTransport transport(station_.Settings().BackendHost());
  DeviceTokenSession tokens(identity_, transport);
  uint64_t disabledUntilMs = 0;
  while (true) {
    // Notifications wake this persistence pass even while HTTP is backed off/disabled.
    const bool persisted = PersistCurrent();
    KeepsakeOutcome outcome;
    const uint64_t nowMs = NowMs();
    if (persisted && outbox_.NextReady(nowMs, outcome) && nowMs >= disabledUntilMs
        && backendOnline_.load() && station_.HasIp() && tokens.EnsureToken(nowMs)) {
      if (outbox_.HasDisposition(outcome.outcomeId)) {
        outbox_.Complete(outcome.outcomeId);
        if (!PersistCurrent()) healthy_.store(false);
        continue;
      }
      int status = 0;
      std::string errorCode;
      bool completed = Deliver(outcome, tokens.AccessToken(NowMs()), status, errorCode);
      if (!completed && status == 401 && tokens.HandleUnauthorized(NowMs())) {
        completed = Deliver(outcome, tokens.AccessToken(NowMs()), status, errorCode);
      }
      if (completed) {
        outbox_.Complete(outcome.outcomeId);
        outbox_.RemovePrepared(outcome.outcomeId);
        if (!PersistCurrent()) healthy_.store(false);
      } else if (PermanentKeepsakeFailure(status, errorCode)) {
        // Commit terminal evidence before advancing: a reboot cannot resurrect this send.
        if (outbox_.RecordDisposition(outcome, errorCode) && PersistCurrent()) {
          outbox_.Complete(outcome.outcomeId);
          outbox_.RemovePrepared(outcome.outcomeId);
          if (!PersistCurrent()) healthy_.store(false);
        } else {
          healthy_.store(false);
          outbox_.Defer(outcome.outcomeId, NowMs());
        }
      } else {
        outbox_.Defer(outcome.outcomeId, NowMs());
        // Blank 404 means feature/route unavailable, never a confirmed deleted outcome.
        if (status == 404 && errorCode.empty()) disabledUntilMs = NowMs() + 30000;
      }
    }
    ulTaskNotifyTake(pdTRUE, pdMS_TO_TICKS(1000));
  }
}

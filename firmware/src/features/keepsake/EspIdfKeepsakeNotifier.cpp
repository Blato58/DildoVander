#include "EspIdfKeepsakeNotifier.h"
#include "KeepsakePersistenceCodec.h"
#include "EspIdfKeepsakeInventoryStorage.h"

#include <array>
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
#include "../device_control/DeviceStorageRuntime.h"

namespace {
uint64_t NowMs() { return static_cast<uint64_t>(esp_timer_get_time() / 1000); }

bool Valid(const KeepsakeOutcome& item) { return keepsake_persistence::Valid(item); }

std::string Body(const KeepsakeOutcome& item, bool persistent) {
  return keepsake_persistence::Body(item, persistent);
}

bool Text(cJSON* root, const char* name, std::string& target, size_t maximum = 36) {
  auto* value = cJSON_GetObjectItemCaseSensitive(root, name);
  if (!cJSON_IsString(value) || !value->valuestring || std::strlen(value->valuestring) > maximum) return false;
  target = value->valuestring;
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

}  // namespace

EspIdfKeepsakeNotifier::EspIdfKeepsakeNotifier(DeviceIdentity& identity, EspIdfStationNetwork& station,
                                           const std::atomic<bool>& backendOnline,
                                           const CommandJournal& ownerJournal, JournalStorage& storage)
    : identity_(identity), station_(station), backendOnline_(backendOnline),
      ownerJournal_(ownerJournal), journalStorage_(&storage) {}

EspIdfKeepsakeNotifier::EspIdfKeepsakeNotifier(DeviceIdentity& identity,
    EspIdfStationNetwork& station, const std::atomic<bool>& backendOnline, DeviceStorageRuntime& storage)
    : identity_(identity), station_(station), backendOnline_(backendOnline),
      ownerJournal_(storage.ControlJournal()), journalStorage_(nullptr),
      bridge_(std::make_unique<KeepsakeEvidenceBridge>(storage.EvidenceRequests(), storage)), sharedStorage_(&storage) {}

bool EspIdfKeepsakeNotifier::Start(bool initialized) {
  if (task_ || !initialized) return false;
  if (bridge_) {
    if (!bridge_->Bootstrap()) return false;
    healthy_.store(true);
    if (xTaskCreate(TaskEntry, "keepsake_outbox", 8192, this, 2, &task_) == pdPASS) return true;
    healthy_.store(false); task_ = nullptr;
    return false;
  }
  if (!Load()) return false;
  // Boot-only persisted snapshot, before the physical control owner runs.
  PersistentCommandJournal savedJournal(*journalStorage_);
  if (!outbox_.RecoverPrepared(savedJournal) || !PersistCurrent()) return false;
  outbox_.CompactPreparationDispositions();
  if (!PersistCurrent()) return false;
  healthy_.store(true);
  if (xTaskCreate(TaskEntry, "keepsake_outbox", 8192, this, 2, &task_) != pdPASS) {
    task_ = nullptr;
    healthy_.store(false);
    return false;
  }
  return true;
}

bool EspIdfKeepsakeNotifier::Prepare(const KeepsakeOutcome& outcome) {
  if (bridge_) return false; // Shared callers must retain a generation-tagged handle.
  if (!healthy_.load() || !Valid(outcome) || outcome.compartment < 1 || outcome.compartment > 6) return false;
  std::lock_guard<std::mutex> lock(persistenceMutex_);
  if (!outbox_.Prepare(outcome)) return false;
  if (Persist(outbox_.State())) return true;
  outbox_.RemovePrepared(outcome.outcomeId, outcome.operationEpoch);
  healthy_.store(false);
  return false;
}

EspIdfKeepsakeNotifier::Preparation EspIdfKeepsakeNotifier::PrepareOwned(
    const KeepsakeOutcome& outcome, Handle& handle) {
  if (!bridge_ || !healthy_.load()) return Preparation::Rejected;
  EvidenceStorageOwner::Request request;
  if (!EvidenceStorageOwner::MakeOnlineRequest(EvidenceStorageOwner::Operation::OnlinePrepare, outcome, request))
    return Preparation::Rejected;
  const auto queued = bridge_->TryPrepare(request.online, handle);
  if (queued == KeepsakeEvidenceBridge::PrepareResult::Busy) return Preparation::Pending;
  if (queued == KeepsakeEvidenceBridge::PrepareResult::Refused) return Preparation::Rejected;
  if (task_) xTaskNotifyGive(task_);
  const auto result = bridge_->PollPrepare(handle);
  if (result == KeepsakeEvidenceBridge::PrepareStatus::Pending) return Preparation::Pending;
  return result == KeepsakeEvidenceBridge::PrepareStatus::DurablePrepared
      ? Preparation::Ready : Preparation::Rejected;
}

bool EspIdfKeepsakeNotifier::CompleteOwned(Handle handle) {
  if (!bridge_ || sharedStorage_->HasPendingJournalWork()) return false;
  const auto queued = bridge_->QueueComplete(handle);
  if (queued != KeepsakeEvidenceBridge::Transfer::RetainedVolatile
      && queued != KeepsakeEvidenceBridge::Transfer::AlreadyRetained) return false;
  if (!bridge_->TransferToNetwork(handle)) return false;
  if (task_) xTaskNotifyGive(task_);
  return true;
}

bool EspIdfKeepsakeNotifier::RetireOwned(Handle handle, const char* reason) {
  // A started late timing-proof write must be adopted before retirement checks
  // the physical history. Observe the shared mailbox, never the control-owned
  // facade whose local envelope is mutated only by the control task.
  if (!bridge_ || sharedStorage_->HasPendingJournalWork()) return false;
  const auto queued = bridge_->QueueRetire(handle, reason);
  if (queued == KeepsakeEvidenceBridge::Transfer::Busy) return false;
  // A cancelled prepare may already own its immutable cancellation reason.
  // Transfer that retained obligation without replacing it with this reason.
  if (!bridge_->TransferToNetwork(handle)) return false;
  if (task_) xTaskNotifyGive(task_);
  return true;
}

KeepsakeQueueResult EspIdfKeepsakeNotifier::Queue(const KeepsakeOutcome& outcome) {
  if (bridge_) return KeepsakeQueueResult::Rejected;
  if (!healthy_.load() || !Valid(outcome)
      || !HasKeepsakePhysicalEvidence(ownerJournal_, outcome))
    return KeepsakeQueueResult::Rejected;
  const auto result = outbox_.Queue(outcome);
  if (result == KeepsakeQueueResult::VolatilePendingPersistence && task_) xTaskNotifyGive(task_);
  return result;
}

void EspIdfKeepsakeNotifier::Retire(const KeepsakeOutcome& outcome, const char* reason) {
  if (bridge_) return;
  if (outbox_.RequestRetirement(outcome, reason) && task_) xTaskNotifyGive(task_);
}

bool EspIdfKeepsakeNotifier::ProcessRetirements() {
  if (!outbox_.HasRetirements()) return true;
  std::lock_guard<std::mutex> lock(persistenceMutex_);
  // Read committed bytes: a failed marker ACK does not prove that its validated
  // completion bytes were absent. No physical journal writes occur here.
  PersistentCommandJournal savedJournal(*journalStorage_);
  return savedJournal.IsHealthy() && outbox_.ProcessRetirements(savedJournal,
      [this](const KeepsakeOutboxState& state) { return Persist(state); });
}

bool EspIdfKeepsakeNotifier::ProcessDeliveryAcknowledgements() {
  std::lock_guard<std::mutex> lock(persistenceMutex_);
  return outbox_.ProcessDeliveryAcknowledgements(
      [this](const KeepsakeOutboxState& state) { return Persist(state); });
}

bool EspIdfKeepsakeNotifier::Load() {
  inventoryStorage_ = std::make_unique<EspIdfKeepsakeInventoryStorage>(true);
  committed_ = std::make_unique<KeepsakeCommittedInventory>(*inventoryStorage_);
  KeepsakeOutboxState state;
  if (!committed_->ReadState(state)) return false;
  if (committed_->IsBound()) outbox_.RetainRetirementLedger();
  return outbox_.Restore(std::move(state.pending), std::move(state.quarantined), std::move(state.prepared));
}

bool EspIdfKeepsakeNotifier::ReadCommitted(JournalEvidenceInventory& inventory) const {
  if (bridge_) { inventory = {}; return false; } // The runtime alone owns source reads.
  std::lock_guard<std::mutex> lock(persistenceMutex_);
  inventory = {};
  if (committed_ && committed_->ReadCommitted(inventory)) return true;
  healthy_.store(false);
  return false;
}

bool EspIdfKeepsakeNotifier::EstablishInventory(const std::string& deviceId, bool serviceAuthorized) {
  if (bridge_) return false;
  std::lock_guard<std::mutex> lock(persistenceMutex_);
  KeepsakeOutboxState saved;
  if (!serviceAuthorized || !committed_ || !committed_->ReadState(saved)
      || keepsake_persistence::Encode(saved) != keepsake_persistence::Encode(outbox_.State())) return false;
  if (!committed_->Establish(deviceId, true)) { healthy_.store(false); return false; }
  outbox_.RetainRetirementLedger();
  return true;
}

bool EspIdfKeepsakeNotifier::PersistCurrent() {
  std::lock_guard<std::mutex> lock(persistenceMutex_);
  return Persist(outbox_.State());
}

bool EspIdfKeepsakeNotifier::Persist(const KeepsakeOutboxState& state) {
  if (committed_ && committed_->Commit(state)) return true;
  healthy_.store(false);
  return false;
}

bool EspIdfKeepsakeNotifier::Deliver(const KeepsakeOutcome& outcome, const char* token, int& status, std::string& errorCode) {
  errorCode.clear();
  const std::string host = station_.Settings().BackendHost();
  Response received;
  status = Post(host, "/v1/keepsakes/outcomes/" + outcome.operationId, Body(outcome, false), outcome.outcomeId, token, received);
  if (status != 201 && status != 200) { errorCode = ErrorCode(received); return false; }
  return keepsake_persistence::MatchesOutcomeAcknowledgement(received.bytes.data(), received.length, outcome);
}

void EspIdfKeepsakeNotifier::TaskEntry(void* context) {
  static_cast<EspIdfKeepsakeNotifier*>(context)->Run();
}

bool EspIdfKeepsakeNotifier::QueueTechnical(const KeepsakeOutcome& outcome) {
  if (!sharedStorage_ || !Valid(outcome) || !outcome.operationEpoch || !outcome.physicalFingerprint) return false;
  std::unique_lock<std::mutex> lock(technicalMutex_, std::try_to_lock);
  if (!lock || technicalPending_.load()) return false;
  technical_ = outcome;
  technicalQueuedAt_ = NowMs();
  technicalRetryAt_ = 0;
  technicalPending_.store(true);
  if (task_) xTaskNotifyGive(task_);
  return true;
}

void EspIdfKeepsakeNotifier::ProcessTechnical(DeviceTokenSession& tokens) {
  if (!technicalPending_.load() || !sharedStorage_) return;
  KeepsakeOutcome outcome;
  uint64_t queued = 0;
  {
    std::unique_lock<std::mutex> lock(technicalMutex_, std::try_to_lock);
    if (!lock || !technicalPending_.load()) return;
    outcome = technical_;
    queued = technicalQueuedAt_;
  }
  const auto now = NowMs();
  bool finished = now < queued || now - queued >= 180000;
  if (!finished && (now < technicalRetryAt_ || !backendOnline_.load() || !station_.HasIp())) return;
  if (!finished) {
    JournalSnapshot snapshot;
    if (!sharedStorage_->ReadJournalSnapshot(snapshot)) return;
    JournalSnapshotView journal(snapshot);
    if (!journal.IsTechnicalTestOperation(outcome.operationEpoch, outcome.physicalOperationId,
            outcome.compartment, outcome.physicalFingerprint) ||
        !journal.WasTimedPresentationCompletedHistorical(outcome.operationEpoch, outcome.physicalOperationId,
            outcome.compartment, outcome.physicalFingerprint)) {
      // No synthetic proof after reset, compaction or an uncertain completion.
      finished = true;
    } else if (tokens.EnsureToken(now)) {
      Response response;
      const int status = Post(station_.Settings().BackendHost(), "/v1/keepsakes/outcomes/" + outcome.operationId,
          Body(outcome, false), outcome.outcomeId, tokens.AccessToken(NowMs()), response);
      finished = status == 201 && keepsake_persistence::MatchesTechnicalOutcomeAcknowledgement(
          response.bytes.data(), response.length, outcome);
      if (status == 401) (void)tokens.HandleUnauthorized(NowMs());
      if (status >= 400 && status < 500 && status != 401 && status != 429) finished = true;
      technicalRetryAt_ = NowMs() + 1000;
    }
  }
  if (finished) {
    std::lock_guard<std::mutex> lock(technicalMutex_);
    technical_ = {};
    technicalPending_.store(false);
  }
}

void EspIdfKeepsakeNotifier::Run() {
  if (bridge_) { RunShared(); return; }
  EspIdfTokenExchangeTransport transport(station_.Settings().BackendHost());
  DeviceTokenSession tokens(identity_, transport);
  uint64_t disabledUntilMs = 0;
  while (true) {
    ProcessTechnical(tokens);
    // Notifications wake this persistence pass even while HTTP is backed off/disabled.
    const bool persisted = ProcessDeliveryAcknowledgements() && ProcessRetirements() && PersistCurrent();
    KeepsakeOutcome outcome;
    const uint64_t nowMs = NowMs();
    if (persisted && outbox_.NextReady(nowMs, outcome) && nowMs >= disabledUntilMs
        && backendOnline_.load() && station_.HasIp() && tokens.EnsureToken(nowMs)) {
      if (outbox_.HasDisposition(outcome)) {
        outbox_.Complete(outcome.outcomeId, outcome.operationEpoch);
        outbox_.RemovePrepared(outcome.outcomeId, outcome.operationEpoch);
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
        // The validated outcome ACK is the whole producer handoff. The backend
        // owns evening association; no phone pickup or private claim is fetched.
        if (!outbox_.RequestDeliveryAcknowledgement(outcome) || !ProcessDeliveryAcknowledgements()) healthy_.store(false);
      } else if (PermanentKeepsakeFailure(status, errorCode)) {
        // Commit terminal evidence before advancing: a reboot cannot resurrect this send.
        if (outbox_.RecordDisposition(outcome, errorCode) && PersistCurrent()) {
          outbox_.Complete(outcome.outcomeId, outcome.operationEpoch);
          outbox_.RemovePrepared(outcome.outcomeId, outcome.operationEpoch);
          if (!PersistCurrent()) healthy_.store(false);
        } else {
          healthy_.store(false);
          outbox_.Defer(outcome.outcomeId, NowMs(), outcome.operationEpoch);
        }
      } else {
        outbox_.Defer(outcome.outcomeId, NowMs(), outcome.operationEpoch);
        // Blank 404 means feature/route unavailable, never a confirmed deleted outcome.
        if (status == 404 && errorCode.empty()) disabledUntilMs = NowMs() + 30000;
      }
    }
    ulTaskNotifyTake(pdTRUE, pdMS_TO_TICKS(1000));
  }
}

void EspIdfKeepsakeNotifier::RunShared() {
  EspIdfTokenExchangeTransport transport(station_.Settings().BackendHost());
  DeviceTokenSession tokens(identity_, transport);
  enum class Action : uint8_t { None, Ack, Retire, Defer };
  Action action = Action::None;
  KeepsakeEvidenceBridge::Pending selected;
  std::string retirementReason;
  uint64_t disabledUntilMs = 0, failedAtMs = 0;
  while (true) {
    ProcessTechnical(tokens);
    bridge_->Pump();
    if (bridge_->IsPoisoned()) healthy_.store(false);
    if (action != Action::None) {
      // Keep a validated response until the fixed mailbox accepts it. A busy
      // metadata lock never turns a real HTTP ACK into another send.
      const auto result = action == Action::Ack ? bridge_->QueueValidatedAck(selected)
          : action == Action::Retire ? bridge_->QueueRetire(selected.handle, retirementReason.c_str())
          : bridge_->QueueDeferred(selected, failedAtMs);
      if (result != KeepsakeEvidenceBridge::Transfer::Busy) action = Action::None;
    }
    const auto nowMs = NowMs();
    if (action == Action::None && healthy_.load() && nowMs >= disabledUntilMs
        && backendOnline_.load() && station_.HasIp() && tokens.EnsureToken(nowMs)
        && bridge_->LookupPending(nowMs, selected)) {
      // Dynamic encoding belongs to the network task, never the control owner.
      const auto& fixed = selected.outcome;
      KeepsakeOutcome outcome;
      outcome.operationId = fixed.operationId.data(); outcome.outcomeId = fixed.outcomeId.data();
      outcome.proposalId = fixed.proposalId.data(); outcome.physicalOperationId = fixed.physicalOperationId.data();
      outcome.bootId = fixed.bootId.data(); outcome.generation = fixed.generation;
      outcome.deviceRevision = fixed.deviceRevision; outcome.compartment = fixed.compartment;
      outcome.operationEpoch = fixed.operationEpoch; outcome.physicalFingerprint = fixed.physicalFingerprint;
      int status = 0;
      std::string errorCode;
      bool completed = Deliver(outcome, tokens.AccessToken(NowMs()), status, errorCode);
      if (!completed && status == 401 && tokens.HandleUnauthorized(NowMs()))
        completed = Deliver(outcome, tokens.AccessToken(NowMs()), status, errorCode);
      if (completed) action = Action::Ack;
      else if (PermanentKeepsakeFailure(status, errorCode)) {
        retirementReason = errorCode; action = Action::Retire;
      } else {
        failedAtMs = NowMs(); action = Action::Defer;
        if (status == 404 && errorCode.empty()) disabledUntilMs = failedAtMs + 30000;
      }
    }
    ulTaskNotifyTake(pdTRUE, pdMS_TO_TICKS(10));
  }
}

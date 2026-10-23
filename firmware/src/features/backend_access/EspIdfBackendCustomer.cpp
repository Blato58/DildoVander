#include "EspIdfBackendNetwork.h"
#include "DeviceTokenSession.h"
#include "EspIdfStationNetwork.h"
#include "CustomerSessionRecovery.h"

#include <cstring>
#include "esp_http_client.h"
#include "esp_crt_bundle.h"
#include "esp_timer.h"
#include "../network_provisioning/NetworkProfiles.h"
#include "../conversation/EspIdfConversationUuid.h"
#include "../conversation/ConversationStartCodec.h"
#include "../provisioning/EspIdfSha256.h"

namespace {
uint64_t MonotonicMs() { return static_cast<uint64_t>(esp_timer_get_time() / 1000); }
void WaitMs(uint32_t durationMs) { vTaskDelay(pdMS_TO_TICKS(durationMs)); }
}

namespace {
const char* CustomerText(cJSON* object, const char* key) {
  const auto* value = cJSON_GetObjectItemCaseSensitive(object, key);
  return cJSON_IsString(value) ? value->valuestring : "";
}
bool CustomerToken(const std::string& value, size_t maximum) {
  if (value.empty() || value.size() > maximum) return false;
  for (char byte : value)
    if (!((byte >= 'a' && byte <= 'z') || (byte >= 'A' && byte <= 'Z') ||
          (byte >= '0' && byte <= '9') || byte == '-' || byte == '_')) return false;
  return true;
}
struct CustomerHttpBody { char bytes[2049]{}; size_t size = 0; bool overflow = false; };
esp_err_t CustomerHttpEvent(esp_http_client_event_t* event) {
  if (event->event_id != HTTP_EVENT_ON_DATA) return ESP_OK;
  auto* body = static_cast<CustomerHttpBody*>(event->user_data);
  if (event->data_len < 0 || static_cast<size_t>(event->data_len) > 2048 - body->size) {
    body->overflow = true; return ESP_FAIL;
  }
  std::memcpy(body->bytes + body->size, event->data, event->data_len);
  body->size += event->data_len; return ESP_OK;
}
// Runs only on the existing network task or the dedicated customer HTTPS task.
int CustomerHttp(const std::string& host, const char* token, const std::string& path,
                 cJSON* request, cJSON*& response) {
  response = nullptr;
  if (!token || !*token) return 0;
  const std::string url = "https://" + host + path;
  CustomerHttpBody received;
  esp_http_client_config_t config{};
  config.url = url.c_str(); config.timeout_ms = 5000;
  config.crt_bundle_attach = esp_crt_bundle_attach;
  config.event_handler = CustomerHttpEvent; config.user_data = &received;
  config.disable_auto_redirect = true;
  auto client = esp_http_client_init(&config);
  if (!client) return 0;
  std::string authorization = "Bearer " + std::string(token);
  char* body = request ? cJSON_PrintUnformatted(request) : nullptr;
  bool configured = esp_http_client_set_header(client, "Authorization", authorization.c_str()) == ESP_OK;
  if (request) configured = configured && body &&
      esp_http_client_set_method(client, HTTP_METHOD_POST) == ESP_OK &&
      esp_http_client_set_header(client, "Content-Type", "application/json") == ESP_OK &&
      esp_http_client_set_post_field(client, body, std::strlen(body)) == ESP_OK;
  const auto result = configured ? esp_http_client_perform(client) : ESP_FAIL;
  const int rawStatus = esp_http_client_get_status_code(client);
  const int status = !received.overflow && (result == ESP_OK || rawStatus == 401) ? rawStatus : 0;
  if (status && received.size) response = cJSON_ParseWithLength(received.bytes, received.size);
  esp_http_client_cleanup(client);
  if (body) { NetworkProfiles::Wipe(body, std::strlen(body)); cJSON_free(body); }
  NetworkProfiles::Wipe(authorization.data(), authorization.size());
  NetworkProfiles::Wipe(received.bytes, sizeof(received.bytes));
  return status;
}
}

cJSON* EspIdfBackendNetwork::CustomerRequest(cJSON* request) {
  if (!cJSON_IsObject(request) || cJSON_GetNumberValue(cJSON_GetObjectItemCaseSensitive(request, "v")) != 1)
    return nullptr;
  for (auto* field = request->child; field; field = field->next) {
    if (!field->string || (std::strcmp(field->string, "v") && std::strcmp(field->string, "op") &&
        std::strcmp(field->string, "ticket") && std::strcmp(field->string, "operationId") &&
        std::strcmp(field->string, "sessionId"))) return nullptr;
  }
  const std::string operation = CustomerText(request, "operationId");
  const std::string op = CustomerText(request, "op");
  std::lock_guard<std::mutex> lock(customerMutex_);
  const char* error = nullptr;
  if (op == "start") {
    // Share the voice reservation lock with operator starts. An atomic busy
    // read alone could race a service start between its check and commit.
    std::lock_guard<std::mutex> voiceLock(voiceMutex_);
    const std::string ticket = CustomerText(request, "ticket");
    std::array<uint8_t, 32> digest{};
    EspIdfSha256 hash;
    const bool valid = IsConversationUuid(operation) && CustomerToken(ticket, 256) &&
        hash.Hash(reinterpret_cast<const uint8_t*>(ticket.data()), ticket.size(), digest);
    if (!valid) error = "invalid_request";
    else if (!customerOperation_.empty() && operation == customerOperation_) {
      // A logical retry survives ticket expiry, but a changed payload never
      // inherits the existing operation's result or queues another start.
      if (digest != customerTicketDigest_) error = "operation_conflict";
    }
    else if (!customerEnabled_.load() || !IsCustomerChallengeCurrent(!customerChallenge_.empty(),
             customerChallengeRegistering_, runtimeState_.Matches(customerChallengeSnapshot_),
             MonotonicMs(), customerChallengeAtMs_, 90000)) error = "unavailable";
    else if (customerReserved_.load() || IsVoiceBusy()) error = "busy";
    else {
      customerOperation_ = operation; customerTicket_ = ticket; customerTicketDigest_ = digest; customerSession_.clear();
      customerState_ = "queued"; customerError_.clear(); customerReported_.clear();
      customerReserved_.store(true);
    }
  } else if (op != "challenge" && op != "status" && op != "end") return nullptr;
  // End is a wake/status hint only. Account authorization and exact owning
  // session are checked by the backend; BLE never grants global Stop.
  auto* response = cJSON_CreateObject();
  cJSON_AddNumberToObject(response, "v", 1);
  cJSON_AddStringToObject(response, "deviceId", identity_.DeviceId().c_str());
  cJSON_AddStringToObject(response, "bootId", bootId_.c_str());
  const bool availableChallenge = customerEnabled_.load() && !customerReserved_.load() &&
      IsCustomerChallengeCurrent(!customerChallenge_.empty(), customerChallengeRegistering_,
          runtimeState_.Matches(customerChallengeSnapshot_), MonotonicMs(), customerChallengeAtMs_, 90000);
  cJSON_AddStringToObject(response, "challenge", availableChallenge ? customerChallenge_.c_str() : "");
  auto* capabilities = cJSON_AddObjectToObject(response, "capabilities");
  cJSON_AddNumberToObject(capabilities, "customerStart", customerEnabled_.load() ? 1 : 0);
  cJSON_AddStringToObject(response, "state", (customerState_ == "queued" || customerState_ == "context_ready") ? "starting" : customerState_ == "finishing" ? "running" : customerState_.c_str());
  if (!operation.empty() && operation == customerOperation_) {
    cJSON_AddStringToObject(response, "operationId", customerOperation_.c_str());
    if (!customerSession_.empty()) cJSON_AddStringToObject(response, "sessionId", customerSession_.c_str());
  }
  if (error || !customerError_.empty()) cJSON_AddStringToObject(response, "error", error ? error : customerError_.c_str());
  return response;
}

void EspIdfBackendNetwork::PollCustomerStart(const ConversationRuntimeSnapshot& snapshot, bool admitted) {
  std::lock_guard<std::mutex> lock(customerMutex_);
  customerIdleAdmission_.store(admitted && !IsVoiceBusy() && !customerReserved_.load());
  if (customerState_ != "queued") return;
  if (admitted && runtimeState_.Matches(customerChallengeSnapshot_) && customerEnabled_.load() &&
      StartVoiceInternal(customerOperation_, snapshot, false, "", true)) {
    customerState_ = "starting";
  } else {
    customerState_ = "idle"; customerError_ = "busy";
    NetworkProfiles::Wipe(customerTicket_.data(), customerTicket_.size()); customerTicket_.clear();
    customerReserved_.store(false);
  }
}

bool EspIdfBackendNetwork::RedeemCustomer(DeviceTokenSession& tokens, const ConversationRuntimeSnapshot&,
                                         std::string& conversation) {
  auto* request = cJSON_CreateObject();
  {
    std::lock_guard<std::mutex> lock(customerMutex_);
    cJSON_AddNumberToObject(request, "protocolVersion", 1);
    cJSON_AddStringToObject(request, "ticket", customerTicket_.c_str());
    cJSON_AddStringToObject(request, "operationId", customerOperation_.c_str());
    cJSON_AddStringToObject(request, "bootId", bootId_.c_str());
    cJSON_AddStringToObject(request, "challenge", customerChallenge_.c_str());
  }
  cJSON* response = nullptr;
  int code = CustomerHttp(settings_.BackendHost(), tokens.AccessToken(MonotonicMs()), "/v1/customer-sessions/redeem", request, response);
  if (code == 401 && tokens.HandleUnauthorized(MonotonicMs())) {
    cJSON_Delete(response); response = nullptr;
    code = CustomerHttp(settings_.BackendHost(), tokens.AccessToken(MonotonicMs()), "/v1/customer-sessions/redeem", request, response);
  }
  auto* ticket = cJSON_GetObjectItemCaseSensitive(request, "ticket");
  if (cJSON_IsString(ticket)) NetworkProfiles::Wipe(ticket->valuestring, std::strlen(ticket->valuestring));
  cJSON_Delete(request);
  std::lock_guard<std::mutex> lock(customerMutex_);
  const std::string id = CustomerText(response, "conversationId");
  const std::string session = CustomerText(response, "customerSessionId");
  const bool accepted = (code == 200 || code == 201) && IsConversationUuid(id) && IsConversationUuid(session);
  if (accepted) {
    conversation = id; customerSession_ = session; customerState_ = "context_ready"; customerError_.clear();
    NetworkProfiles::Wipe(customerTicket_.data(), customerTicket_.size()); customerTicket_.clear();
  } else {
    customerState_ = code >= 400 && code < 500 ? "failed" : "recovering";
    customerError_ = code >= 400 && code < 500 ? "start_rejected" : "start_unknown";
  }
  cJSON_Delete(response);
  return accepted;
}

void EspIdfBackendNetwork::CustomerTaskEntry(void* context) {
  static_cast<EspIdfBackendNetwork*>(context)->RunCustomer();
}
bool EspIdfBackendNetwork::RefreshCustomerChallenge(const char* accessToken) {
  ConversationRuntimeSnapshot snapshot;
  if (!runtimeState_.Read(snapshot)) return false;
  std::string challenge;
  {
    std::lock_guard<std::mutex> lock(customerMutex_);
    if (customerReserved_.load()) return !customerChallenge_.empty();
    if (IsCustomerChallengeCurrent(!customerChallenge_.empty(), customerChallengeRegistering_,
        runtimeState_.Matches(customerChallengeSnapshot_), MonotonicMs(), customerChallengeAtMs_, 60000)) return true;
    challenge = GenerateEspIdfConversationUuid();
    customerChallengeRegistering_ = true;
  }
  auto* request = cJSON_CreateObject();
  cJSON_AddNumberToObject(request, "protocolVersion", 1);
  cJSON_AddStringToObject(request, "bootId", bootId_.c_str());
  cJSON_AddStringToObject(request, "challenge", challenge.c_str());
  cJSON_AddNumberToObject(request, "generation", snapshot.generation);
  cJSON_AddStringToObject(request, "catalogVersion", snapshot.catalogVersion.c_str());
  cJSON_AddStringToObject(request, "operationEpoch", std::to_string(snapshot.operationEpoch).c_str());
  cJSON* response = nullptr;
  const int registered = CustomerHttp(settings_.BackendHost(), accessToken, "/v1/customer-sessions/device-challenge", request, response);
  cJSON_Delete(request); cJSON_Delete(response);
  if (registered == 401) showUnauthorized_.store(true);
  std::lock_guard<std::mutex> lock(customerMutex_);
  customerChallengeRegistering_ = false;
  const bool accepted = registered == 200 || registered == 201 || registered == 204;
  const bool current = accepted && !customerReserved_.load() && runtimeState_.Matches(snapshot);
  if (current) {
    customerChallenge_ = challenge; customerChallengeAtMs_ = MonotonicMs(); customerChallengeSnapshot_ = snapshot;
  }
  return current;
}
void EspIdfBackendNetwork::RunCustomer() {
  for (;;) {
    BorrowedShowToken::Copy token;
    if (!online_.load() || !station_.HasIp() || !showToken_.Read(MonotonicMs(), token)) {
      customerEnabled_.store(false); WaitMs(1000); continue;
    }
    // Only a control-owner-confirmed idle at boot can retire an old boot lease.
    // No reconnect, timeout, or uncertain accepted start reaches this path.
    if (!customerBootReconciled_) {
      customerEnabled_.store(false);
      const bool ownerIdle = customerIdleAdmission_.load() && !customerReserved_.load() && !IsVoiceBusy();
      customerBootReconciled_ = ReconcileCustomerBoot(ownerIdle,
          [&]() { return RefreshCustomerChallenge(token.bytes.data()); },
          [&]() {
            auto* request = cJSON_CreateObject();
            cJSON_AddStringToObject(request, "bootId", bootId_.c_str());
            cJSON* idleResponse = nullptr;
            const int idle = CustomerHttp(settings_.BackendHost(), token.bytes.data(), "/v1/customer-sessions/device-idle", request, idleResponse);
            cJSON_Delete(request); cJSON_Delete(idleResponse);
            if (idle == 401) showUnauthorized_.store(true);
            return idle == 200 || idle == 204;
          });
    }
    if (!customerBootReconciled_) {
      customerEnabled_.store(false); WaitMs(1000); continue;
    }
    cJSON* response = nullptr;
    bool runtimeReady = false;
    {
      std::lock_guard<std::mutex> lock(customerMutex_);
      runtimeReady = CanPublishCustomerReadiness(customerIdleAdmission_.load(),
          customerReserved_.load(), IsVoiceBusy(), customerBootReconciled_,
          IsCustomerChallengeCurrent(!customerChallenge_.empty(), customerChallengeRegistering_,
              runtimeState_.Matches(customerChallengeSnapshot_), MonotonicMs(), customerChallengeAtMs_, 60000));
    }
    const std::string statusPath = "/v1/customer-sessions/device?bootId=" + bootId_ +
        "&ready=" + (runtimeReady ? "true" : "false");
    const int code = CustomerHttp(settings_.BackendHost(), token.bytes.data(), statusPath, nullptr, response);
    if (code == 401) showUnauthorized_.store(true);
    const bool enabled = code == 200 && cJSON_IsTrue(cJSON_GetObjectItemCaseSensitive(response, "enabled"));
    customerEnabled_.store(enabled);
    const std::string active = CustomerText(response, "activeSessionId");
    const std::string boot = CustomerText(response, "bootId");
    bool stop = false;
    {
      std::lock_guard<std::mutex> lock(customerMutex_);
      bool physicalOutstanding = false;
      {
        std::lock_guard<std::mutex> presentationLock(presentationMutex_);
        physicalOutstanding = terminalProposalOutstanding_ || presentationActive_ || presentationClaimed_;
      }
      if (customerState_ == "finishing" && !physicalOutstanding) {
        customerState_ = customerPresentationResult_.load() == 1 ? "completed" : "failed";
        if (customerState_ == "failed") customerError_ = "presentation_not_completed";
      }
      if (!active.empty() && active != customerSession_) {
        // Lost redemption replies cannot launch a second provider session. Once
        // the durable operation is identified and local audio is inactive,
        // report failure so the backend closes that exact conversation first.
        const bool ownUnknown = CanRetireUnknownCustomerStart(customerState_, bootId_, customerOperation_,
            IsVoiceBusy(), boot, CustomerText(response, "operationId"));
        if (ownUnknown) { customerSession_ = active; customerState_ = "failed"; }
        else { customerReserved_.store(true); customerState_ = "recovering"; customerError_ = "session_reconciliation_required"; }
      }
      if (customerState_ == "starting" && !IsVoiceBusy() && customerSession_.empty()) {
        customerState_ = "failed"; customerError_ = "start_cancelled";
      }
      if (code == 200 && active.empty() && customerState_ == "failed" && customerSession_.empty()) {
        customerReserved_.store(false); customerChallenge_.clear();
        NetworkProfiles::Wipe(customerTicket_.data(), customerTicket_.size()); customerTicket_.clear();
      }
      stop = !customerSession_.empty() && active == customerSession_ && boot == bootId_ &&
          cJSON_IsTrue(cJSON_GetObjectItemCaseSensitive(response, "endRequested"));
    }
    cJSON_Delete(response);
    if (stop) StopVoice();
    RefreshCustomerChallenge(token.bytes.data());
    std::string session, state;
    {
      std::lock_guard<std::mutex> lock(customerMutex_);
      session = customerSession_; state = customerState_;
      if (state == customerReported_) state.clear();
    }
    if (!session.empty() && (state == "context_ready" || state == "running" || state == "completed" || state == "failed")) {
      auto* request = cJSON_CreateObject();
      cJSON_AddStringToObject(request, "bootId", bootId_.c_str()); cJSON_AddStringToObject(request, "state", state.c_str());
      response = nullptr;
      const int reported = CustomerHttp(settings_.BackendHost(), token.bytes.data(), "/v1/customer-sessions/" + session + "/device-state", request, response);
      cJSON_Delete(request); cJSON_Delete(response);
      if (reported == 401) showUnauthorized_.store(true);
      if (reported == 200 || reported == 204) {
        std::lock_guard<std::mutex> lock(customerMutex_);
        customerReported_ = state;
        if ((state == "completed" || state == "failed") && customerState_ == state) {
          customerReserved_.store(false); customerChallenge_.clear();
        }
      }
    }
    WaitMs(1000);
  }
}

#include "EspIdfBackendNetwork.h"

#include <algorithm>
#include <ctime>

#include "esp_log.h"
#include "esp_netif_sntp.h"
#include "esp_random.h"
#include "esp_timer.h"

#include "BuildDate.h"
#include "DeviceTokenSession.h"
#include "EspIdfNetworkSettings.h"
#include "EspIdfTokenExchangeTransport.h"
#include "EspIdfTrustedTimeStore.h"
#include "EspIdfWifiStation.h"
#include "ReconnectPolicy.h"
#include "TrustedTime.h"
#include "../conversation/ConversationStartCoordinator.h"
#include "../conversation/EspIdfConversationStartTransport.h"
#include "../conversation/EspIdfConversationTextTransport.h"
#include "../conversation/EspIdfConversationUuid.h"
#include "../conversation/EspIdfTurnPollTransport.h"
#include "../conversation/TurnPollSchedule.h"

namespace {

constexpr char LogTag[] = "kufr_network";
constexpr uint32_t ConnectionTimeoutMs = 15000;
constexpr uint32_t TokenCheckPeriodMs = 1000;
constexpr uint32_t OfflineRetryMs = 30000;
constexpr uint32_t PollCheckPeriodMs = 100;
constexpr char NeutralCatalogVersion[] = "neutral-1";

uint64_t MonotonicMs() {
  return static_cast<uint64_t>(esp_timer_get_time() / 1000);
}

void WaitMs(uint32_t durationMs) {
  vTaskDelay(pdMS_TO_TICKS(durationMs));
}

}  // namespace

EspIdfBackendNetwork::EspIdfBackendNetwork(
    DeviceIdentity& identity, PersistentCommandJournal& journal)
    : identity_(identity), journal_(journal) {}

bool EspIdfBackendNetwork::QueueTextTurn(const std::string& text) {
  return online_.load() && textInbox_.Submit(text);
}

bool EspIdfBackendNetwork::Start(bool nvsInitialized) {
  if (task_ != nullptr || !identity_.IsProvisioned() ||
      LoadEspIdfNetworkSettings(settings_, nvsInitialized) !=
          NetworkSettingsLoadResult::Loaded) {
    return false;
  }

  if (xTaskCreate(TaskEntry, "backend_network", 12288, this, 4, &task_) !=
      pdPASS) {
    settings_.Reset();
    task_ = nullptr;
    return false;
  }
  return true;
}

void EspIdfBackendNetwork::TaskEntry(void* argument) {
  EspIdfBackendNetwork* network =
      static_cast<EspIdfBackendNetwork*>(argument);
  network->Run();
  network->online_.store(false);
  network->textInbox_.Clear();
  network->task_ = nullptr;
  vTaskDelete(nullptr);
}

void EspIdfBackendNetwork::Run() {
  EspIdfWifiStation wifi;
  if (!wifi.Start(settings_)) {
    ESP_LOGE(LogTag, "Wi-Fi initialization failed; backend offline");
    return;
  }

  EspIdfTrustedTimeStore timeStore(true);
  const uint64_t buildDate = ParseBuildDateUtc(__DATE__);
  TrustedTime trustedTime(timeStore, buildDate);
  if (buildDate == 0 || !trustedTime.IsHealthy()) {
    ESP_LOGE(LogTag, "Time trust unavailable; backend offline");
    return;
  }

  EspIdfTokenExchangeTransport transport(settings_.BackendHost());
  DeviceTokenSession tokens(identity_, transport);
  EspIdfConversationStartTransport startTransport(settings_.BackendHost());
  ConversationStartCoordinator coordinator(identity_, tokens, startTransport);
  ConversationStartRequest startRequest;
  startRequest.deviceId = identity_.DeviceId();
  startRequest.catalogVersion = NeutralCatalogVersion;
  startRequest.generation = 0;
  ReconnectPolicy reconnect;
  bool sntpStarted = false;
  bool timeSynchronized = false;
  bool revokedLogged = false;
  while (true) {
    if (!wifi.HasIp()) {
      online_.store(false);
      textInbox_.Clear();
      if (!wifi.ConnectAndWait(ConnectionTimeoutMs)) {
        WaitMs(reconnect.NextDelayMs(esp_random() % 401));
        continue;
      }
      reconnect.Reset();
      if (bootId_.empty()) {
        bootId_ = GenerateEspIdfConversationUuid();
        startRequest.bootId = bootId_;
        startRequest.idempotencyKey = GenerateEspIdfConversationUuid();
      }
      ESP_LOGI(LogTag, "Wi-Fi connected; waiting for trusted time");
    }

    if (!sntpStarted) {
      esp_sntp_config_t config =
          ESP_NETIF_SNTP_DEFAULT_CONFIG("pool.ntp.org");
      if (esp_netif_sntp_init(&config) != ESP_OK) {
        WaitMs(OfflineRetryMs);
        continue;
      }
      sntpStarted = true;
    }
    if (!timeSynchronized) {
      if (esp_netif_sntp_sync_wait(pdMS_TO_TICKS(10000)) != ESP_OK ||
          !wifi.HasIp()) {
        online_.store(false);
        textInbox_.Clear();
        WaitMs(OfflineRetryMs);
        continue;
      }
      timeSynchronized = true;
    }

    const std::time_t currentTime = std::time(nullptr);
    if (currentTime <= 0 ||
        !trustedTime.CheckAndRecord(static_cast<uint64_t>(currentTime))) {
      online_.store(false);
      textInbox_.Clear();
      ESP_LOGW(LogTag, "Time not trusted; backend offline");
      WaitMs(OfflineRetryMs);
      continue;
    }

    const uint64_t nowMs = MonotonicMs();
    if (!tokens.EnsureToken(nowMs)) {
      online_.store(false);
      textInbox_.Clear();
      if (tokens.IsRevoked() && !revokedLogged) {
        ESP_LOGE(LogTag, "Device credential rejected; backend offline");
        revokedLogged = true;
      }
      WaitMs(OfflineRetryMs);
      continue;
    }
    online_.store(true);
    std::string text;
    if (textInbox_.Take(text)) {
      RunTextTurn(text, tokens, wifi, coordinator, startRequest);
      std::fill(text.begin(), text.end(), '\0');
      text.clear();
    }
    WaitMs(TokenCheckPeriodMs);
  }
}

void EspIdfBackendNetwork::RunTextTurn(
    const std::string& text, DeviceTokenSession& tokens,
    EspIdfWifiStation& wifi,
    ConversationStartCoordinator& coordinator,
    ConversationStartRequest& startRequest) {
  if (!wifi.HasIp() || !journal_.IsHealthy() || journal_.HasPending() ||
      bootId_.empty()) {
    ESP_LOGW(LogTag, "Text turn dropped: device state unavailable");
    return;
  }

  // Until local control is connected, this read-only IDF runtime has revision
  // zero. The journal is its sole source of compartment availability.
  const uint64_t deviceRevision = 0;
  const uint8_t readyMask = journal_.ReadyMask();
  EspIdfConversationTextTransport textTransport(settings_.BackendHost());
  EspIdfTurnPollTransport pollTransport(settings_.BackendHost());
  if (!textTransport.IsConfigured() ||
      !pollTransport.IsConfigured()) {
    return;
  }

  const ConversationStartOutcome startOutcome =
      coordinator.Start(startRequest, MonotonicMs());
  if (startOutcome != ConversationStartOutcome::Started &&
      startOutcome != ConversationStartOutcome::AlreadyStarted) {
    ESP_LOGW(LogTag, "Text turn: conversation start failed");
    return;
  }

  ConversationTextTurnRequest turnRequest;
  turnRequest.conversationId = coordinator.ConversationId();
  turnRequest.clientTurnId = GenerateEspIdfConversationUuid();
  turnRequest.bootId = bootId_;
  turnRequest.idempotencyKey = GenerateEspIdfConversationUuid();
  turnRequest.catalogVersion = NeutralCatalogVersion;
  turnRequest.text = text;
  turnRequest.generation = 0;
  turnRequest.deviceRevision = deviceRevision;
  turnRequest.readyMask = readyMask;
  const uint64_t turnStartedAtMs = MonotonicMs();
  ConversationTextHttpResult accepted =
      textTransport.Submit(turnRequest, tokens.AccessToken(turnStartedAtMs));
  if (accepted.statusCode == 401 &&
      tokens.HandleUnauthorized(MonotonicMs())) {
    accepted = textTransport.Submit(
        turnRequest, tokens.AccessToken(MonotonicMs()));
  }
  std::fill(turnRequest.text.begin(), turnRequest.text.end(), '\0');
  turnRequest.text.clear();
  if (accepted.statusCode != 202 || accepted.operationId.empty()) {
    if (accepted.statusCode == 404) {
      coordinator.Clear();
      startRequest.idempotencyKey = GenerateEspIdfConversationUuid();
    }
    ESP_LOGW(LogTag, "Text turn: submission failed (%d)",
             accepted.statusCode);
    return;
  }

  TurnCorrelation expected;
  expected.deviceId = identity_.DeviceId();
  expected.bootId = bootId_;
  expected.conversationId = turnRequest.conversationId;
  expected.clientTurnId = turnRequest.clientTurnId;
  expected.generation = turnRequest.generation;
  expected.deviceRevision = deviceRevision;
  TurnPollSchedule schedule;
  schedule.Begin(turnStartedAtMs, MonotonicMs());
  while (wifi.HasIp() && schedule.IsActive()) {
    const uint64_t nowMs = MonotonicMs();
    if (schedule.IsExpired(nowMs) || !journal_.IsHealthy() ||
        journal_.HasPending() || journal_.ReadyMask() != readyMask) {
      break;
    }
    if (!schedule.MarkStarted(nowMs)) {
      WaitMs(PollCheckPeriodMs);
      continue;
    }
    TurnPollHttpResult result = pollTransport.Poll(
        accepted.operationId, expected, 0, deviceRevision,
        tokens.AccessToken(nowMs));
    if (result.statusCode == 401 &&
        tokens.HandleUnauthorized(MonotonicMs())) {
      result = pollTransport.Poll(
          accepted.operationId, expected, 0, deviceRevision,
          tokens.AccessToken(MonotonicMs()));
    }
    const uint64_t completedAtMs = MonotonicMs();
    if (result.statusCode == 200 &&
        result.decision == TurnPollDecision::ReplyReady &&
        !schedule.IsExpired(completedAtMs) && wifi.HasIp() &&
        journal_.IsHealthy() && !journal_.HasPending() &&
        journal_.ReadyMask() == readyMask) {
      ESP_LOGI(LogTag, "Fake text reply accepted without actuator action");
      std::fill(result.reply.begin(), result.reply.end(), '\0');
      break;
    }
    const bool stillPending = result.statusCode == 200 &&
                              result.decision == TurnPollDecision::Wait;
    schedule.MarkCompleted(completedAtMs, stillPending);
  }
}

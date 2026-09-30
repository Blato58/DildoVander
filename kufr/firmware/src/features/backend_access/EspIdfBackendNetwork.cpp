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
#include "../conversation/TurnPollGuard.h"

namespace {

constexpr char LogTag[] = "kufr_network";
constexpr uint32_t ConnectionTimeoutMs = 15000;
constexpr uint32_t TokenCheckPeriodMs = 1000;
constexpr uint32_t OfflineRetryMs = 30000;
constexpr uint32_t PollCheckPeriodMs = 100;
constexpr uint64_t ConversationIdleMs = 90000;

uint64_t MonotonicMs() {
  return static_cast<uint64_t>(esp_timer_get_time() / 1000);
}

uint64_t ConservativeUtcSeconds(uint64_t trustedUtcAtStart,
                                 uint64_t monotonicAtStartMs,
                                 uint64_t nowMs) {
  const std::time_t wallTime = std::time(nullptr);
  if (wallTime <= 0 || nowMs < monotonicAtStartMs) {
    return UINT64_MAX;
  }
  // A backward wall-clock adjustment must not extend a proposal's lifetime.
  const uint64_t monotonicLowerBound =
      trustedUtcAtStart + (nowMs - monotonicAtStartMs) / 1000;
  return std::max(static_cast<uint64_t>(wallTime), monotonicLowerBound);
}

void WaitMs(uint32_t durationMs) {
  vTaskDelay(pdMS_TO_TICKS(durationMs));
}

}  // namespace

EspIdfBackendNetwork::EspIdfBackendNetwork(
    DeviceIdentity& identity, ConversationRuntimeState& runtimeState)
    : identity_(identity), runtimeState_(runtimeState) {}

bool EspIdfBackendNetwork::QueueTextTurn(
    const std::string& text,
    const ConversationRuntimeSnapshot& expectedSnapshot) {
  {
    std::lock_guard<std::mutex> lock(presentationMutex_);
    if (terminalProposalOutstanding_) {
      return false;
    }
  }
  return online_.load() &&
         textInbox_.Submit(text, expectedSnapshot, runtimeState_);
}

bool EspIdfBackendNetwork::QueueOpening(
    const ConversationRuntimeSnapshot& expectedSnapshot) {
  if (!online_.load() || expectedSnapshot.version == 0) {
    return false;
  }
  {
    std::lock_guard<std::mutex> lock(presentationMutex_);
    if (presentationActive_) {
      return false;
    }
  }
  std::lock_guard<std::mutex> lock(openingMutex_);
  if (openingPending_ || !runtimeState_.Matches(expectedSnapshot)) {
    return false;
  }
  openingSnapshotVersion_ = expectedSnapshot.version;
  openingPending_ = true;
  return true;
}

void EspIdfBackendNetwork::QueueCancel() {
  cancelRequested_.store(true);
  textInbox_.Clear();
  pendingProposal_.Clear();
  std::lock_guard<std::mutex> lock(openingMutex_);
  openingPending_ = false;
}

bool EspIdfBackendNetwork::ReadPendingProposal(
    TurnProposal& proposal, uint64_t nowMs) const {
  return pendingProposal_.Read(proposal, runtimeState_, nowMs);
}

bool EspIdfBackendNetwork::BeginPendingPresentation(
    WandPresentation& presentation, uint64_t nowMs) {
  std::lock_guard<std::mutex> lock(presentationMutex_);
  if (!terminalProposalOutstanding_ || presentationActive_ ||
      !presentation.Start(pendingProposal_, runtimeState_, nowMs)) {
    return false;
  }
  presentationActive_ = true;
  return true;
}

bool EspIdfBackendNetwork::FinishPresentation(
    const WandPresentation& presentation) {
  {
    std::lock_guard<std::mutex> lock(presentationMutex_);
    if (!presentationActive_ ||
        (presentation.State() != WandPresentationState::Idle &&
         presentation.State() != WandPresentationState::Fault)) {
      return false;
    }
    presentationActive_ = false;
    terminalProposalOutstanding_ = false;
  }
  QueueCancel();
  return true;
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
  ReconnectPolicy reconnect;
  bool sntpStarted = false;
  bool timeSynchronized = false;
  bool revokedLogged = false;
  uint64_t lastTurnAtMs = 0;
  while (true) {
    if (!wifi.HasIp()) {
      online_.store(false);
      textInbox_.Clear();
      pendingProposal_.Clear();
      {
        std::lock_guard<std::mutex> lock(openingMutex_);
        openingPending_ = false;
      }
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
    EspIdfConversationTextTransport sessionTransport(settings_.BackendHost());
    if (cancelRequested_.exchange(false)) {
      CloseConversation(coordinator, sessionTransport, tokens, startRequest);
      lastTurnAtMs = 0;
    }
    if (closePending_) {
      if (MonotonicMs() >= closeRetryAtMs_) {
        CloseConversation(coordinator, sessionTransport, tokens, startRequest);
      }
      if (closePending_) {
        WaitMs(TokenCheckPeriodMs);
        continue;
      }
    }
    bool proposalExpired = false;
    {
      std::lock_guard<std::mutex> lock(presentationMutex_);
      TurnProposal candidate;
      proposalExpired = terminalProposalOutstanding_ &&
                        !presentationActive_ &&
                        !pendingProposal_.Read(candidate, runtimeState_,
                                               MonotonicMs());
    }
    if (proposalExpired) {
      CloseConversation(coordinator, sessionTransport, tokens, startRequest);
      WaitMs(TokenCheckPeriodMs);
      continue;
    }
    bool presentationActive = false;
    {
      std::lock_guard<std::mutex> lock(presentationMutex_);
      presentationActive = presentationActive_;
    }
    if (!presentationActive && !coordinator.ConversationId().empty() &&
        lastTurnAtMs != 0 &&
        MonotonicMs() - lastTurnAtMs >= ConversationIdleMs) {
      CloseConversation(coordinator, sessionTransport, tokens, startRequest);
      lastTurnAtMs = 0;
      if (closePending_) {
        WaitMs(TokenCheckPeriodMs);
        continue;
      }
    }
    bool opening = false;
    uint64_t openingVersion = 0;
    {
      std::lock_guard<std::mutex> lock(openingMutex_);
      if (openingPending_) {
        opening = true;
        openingVersion = openingSnapshotVersion_;
        openingPending_ = false;
      }
    }
    std::string text;
    uint64_t snapshotVersion = 0;
    if (opening) {
      RunTurn("", true, openingVersion, tokens, wifi, coordinator,
              startRequest);
      lastTurnAtMs = MonotonicMs();
    } else if (textInbox_.Take(text, snapshotVersion)) {
      RunTurn(text, false, snapshotVersion, tokens, wifi, coordinator,
              startRequest);
      lastTurnAtMs = MonotonicMs();
      std::fill(text.begin(), text.end(), '\0');
      text.clear();
    }
    WaitMs(TokenCheckPeriodMs);
  }
}

void EspIdfBackendNetwork::RunTurn(
    const std::string& text, bool opening, uint64_t snapshotVersion,
    DeviceTokenSession& tokens,
    EspIdfWifiStation& wifi,
    ConversationStartCoordinator& coordinator,
    ConversationStartRequest& startRequest) {
  ConversationRuntimeSnapshot snapshot;
  if (!wifi.HasIp() || !runtimeState_.Read(snapshot) ||
      snapshot.version != snapshotVersion || bootId_.empty()) {
    ESP_LOGW(LogTag, "Text turn dropped: device state unavailable");
    return;
  }

  if (startSnapshotVersion_ != snapshot.version) {
    EspIdfConversationTextTransport closeTransport(settings_.BackendHost());
    if (!CloseConversation(coordinator, closeTransport, tokens,
                           startRequest)) {
      return;
    }
    startRequest.idempotencyKey = GenerateEspIdfConversationUuid();
    startSnapshotVersion_ = snapshot.version;
  }
  if (opening && !coordinator.ConversationId().empty()) {
    EspIdfConversationTextTransport closeTransport(settings_.BackendHost());
    if (!CloseConversation(coordinator, closeTransport, tokens,
                           startRequest)) {
      return;
    }
  }
  startRequest.generation = snapshot.generation;
  startRequest.catalogVersion = snapshot.catalogVersion;
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
  if (!runtimeState_.Matches(snapshot)) {
    return;
  }

  ConversationTextTurnRequest turnRequest;
  turnRequest.conversationId = coordinator.ConversationId();
  turnRequest.clientTurnId = GenerateEspIdfConversationUuid();
  turnRequest.bootId = bootId_;
  turnRequest.idempotencyKey = GenerateEspIdfConversationUuid();
  turnRequest.catalogVersion = snapshot.catalogVersion;
  turnRequest.text = text;
  turnRequest.generation = snapshot.generation;
  turnRequest.deviceRevision = snapshot.deviceRevision;
  turnRequest.readyMask = snapshot.readyMask;
  const uint64_t turnStartedAtMs = MonotonicMs();
  const std::time_t trustedUtcAtStart = std::time(nullptr);
  if (trustedUtcAtStart <= 0) {
    CloseConversation(coordinator, textTransport, tokens, startRequest);
    return;
  }
  ConversationTextHttpResult accepted = opening
      ? textTransport.SubmitOpening(turnRequest, tokens.AccessToken(turnStartedAtMs))
      : textTransport.Submit(turnRequest, tokens.AccessToken(turnStartedAtMs));
  if (accepted.statusCode == 401 &&
      tokens.HandleUnauthorized(MonotonicMs())) {
    accepted = opening
        ? textTransport.SubmitOpening(turnRequest, tokens.AccessToken(MonotonicMs()))
        : textTransport.Submit(turnRequest, tokens.AccessToken(MonotonicMs()));
  }
  if ((accepted.statusCode == 0 || accepted.statusCode >= 500) &&
      wifi.HasIp() && runtimeState_.Matches(snapshot) &&
      MonotonicMs() - turnStartedAtMs < TurnPollSchedule::DeadlineMs) {
    // An accepted 202 response may have been lost. Reuse the exact envelope.
    accepted = opening
        ? textTransport.SubmitOpening(turnRequest, tokens.AccessToken(MonotonicMs()))
        : textTransport.Submit(turnRequest, tokens.AccessToken(MonotonicMs()));
  }
  std::fill(turnRequest.text.begin(), turnRequest.text.end(), '\0');
  turnRequest.text.clear();
  if (accepted.statusCode != 202 || accepted.operationId.empty()) {
    if (accepted.statusCode == 404) {
      coordinator.Clear();
      startRequest.idempotencyKey = GenerateEspIdfConversationUuid();
    } else {
      CloseConversation(coordinator, textTransport, tokens, startRequest);
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
  expected.deviceRevision = snapshot.deviceRevision;
  TurnPollSchedule schedule;
  schedule.Begin(turnStartedAtMs, MonotonicMs());
  bool replyConsumed = false;
  bool noSelection = false;
  while (wifi.HasIp() && schedule.IsActive() &&
         !cancelRequested_.load()) {
    const uint64_t nowMs = MonotonicMs();
    if (schedule.IsExpired(nowMs) || !runtimeState_.Matches(snapshot) ||
        cancelRequested_.load()) {
      break;
    }
    if (!schedule.MarkStarted(nowMs)) {
      WaitMs(PollCheckPeriodMs);
      continue;
    }
    TurnPollHttpResult result = pollTransport.Poll(
        accepted.operationId, expected, snapshot.generation,
        snapshot.deviceRevision, snapshot.catalogVersion,
        snapshot.readyMask,
        ConservativeUtcSeconds(static_cast<uint64_t>(trustedUtcAtStart),
                               turnStartedAtMs, nowMs),
        tokens.AccessToken(nowMs));
    if (result.statusCode == 401 &&
        tokens.HandleUnauthorized(MonotonicMs())) {
      result = pollTransport.Poll(
          accepted.operationId, expected, snapshot.generation,
          snapshot.deviceRevision, snapshot.catalogVersion,
          snapshot.readyMask,
          ConservativeUtcSeconds(static_cast<uint64_t>(trustedUtcAtStart),
                                 turnStartedAtMs, MonotonicMs()),
          tokens.AccessToken(MonotonicMs()));
    }
    const uint64_t completedAtMs = MonotonicMs();
    if (result.statusCode == 200 &&
        (result.decision == TurnPollDecision::ReplyReady ||
         result.decision == TurnPollDecision::ProposalReady) &&
        !schedule.IsExpired(completedAtMs) && wifi.HasIp() &&
        !cancelRequested_.load() &&
        runtimeState_.Matches(snapshot)) {
      if (result.decision == TurnPollDecision::ProposalReady) {
        // The guard already checked UTC expiry. Local monotonic TTL is capped
        // independently and the control owner must check the live snapshot.
        const bool stored = pendingProposal_.Store(
            result.proposal, snapshot, runtimeState_, completedAtMs,
            ProposalRemainingMs(
                result.proposal,
                ConservativeUtcSeconds(
                    static_cast<uint64_t>(trustedUtcAtStart),
                    turnStartedAtMs, completedAtMs)));
        if (stored) {
          std::lock_guard<std::mutex> lock(presentationMutex_);
          terminalProposalOutstanding_ = true;
          ESP_LOGI(LogTag, "Validated proposal held for presentation owner");
        } else {
          CloseConversation(coordinator, textTransport, tokens, startRequest);
        }
      } else {
        pendingProposal_.Clear();
      }
      std::fill(result.reply.begin(), result.reply.end(), '\0');
      replyConsumed = true;
      noSelection = result.noSelection;
      break;
    }
    const bool stillPending = result.statusCode == 200 &&
                              result.decision == TurnPollDecision::Wait;
    schedule.MarkCompleted(completedAtMs, stillPending);
  }
  if (!replyConsumed || noSelection) {
    CloseConversation(coordinator, textTransport, tokens, startRequest);
  }
}

bool EspIdfBackendNetwork::CloseConversation(
    ConversationStartCoordinator& coordinator,
    EspIdfConversationTextTransport& transport,
    DeviceTokenSession& tokens,
    ConversationStartRequest& startRequest) {
  if (!coordinator.ConversationId().empty()) {
    const uint64_t nowMs = MonotonicMs();
    if (deleteIdempotencyKey_.empty()) {
      deleteIdempotencyKey_ = GenerateEspIdfConversationUuid();
    }
    int status = transport.Delete(coordinator.ConversationId(),
                                  deleteIdempotencyKey_,
                                  tokens.AccessToken(nowMs));
    if (status == 401 && tokens.HandleUnauthorized(MonotonicMs())) {
      status = transport.Delete(coordinator.ConversationId(),
                                deleteIdempotencyKey_,
                                tokens.AccessToken(MonotonicMs()));
    }
    ESP_LOGI(LogTag, "Conversation cleanup status %d", status);
    if (status != 204 && status != 404) {
      closePending_ = true;
      closeRetryAtMs_ = nowMs + closeRetryDelayMs_;
      closeRetryDelayMs_ = std::min<uint32_t>(closeRetryDelayMs_ * 2, 30000);
      return false;
    }
  }
  coordinator.Clear();
  pendingProposal_.Clear();
  {
    std::lock_guard<std::mutex> lock(presentationMutex_);
    terminalProposalOutstanding_ = false;
  }
  startRequest.idempotencyKey = GenerateEspIdfConversationUuid();
  deleteIdempotencyKey_.clear();
  closePending_ = false;
  closeRetryAtMs_ = 0;
  closeRetryDelayMs_ = 1000;
  return true;
}

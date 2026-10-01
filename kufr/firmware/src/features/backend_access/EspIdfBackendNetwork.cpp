#include "EspIdfBackendNetwork.h"

#include <algorithm>
#include <ctime>
#include <utility>

#include "esp_log.h"
#include "esp_netif_sntp.h"
#include "esp_random.h"
#include "esp_timer.h"

#include "BuildDate.h"
#include "../voice_turn/EspIdfRealtimeTransport.h"
#include "DeviceTokenSession.h"
#include "EspIdfStationNetwork.h"
#include "EspIdfTokenExchangeTransport.h"
#include "EspIdfTrustedTimeStore.h"
#include "TrustedTime.h"
#include "../conversation/ConversationStartCoordinator.h"
#include "../conversation/ConversationStartCodec.h"
#include "../conversation/EspIdfConversationStartTransport.h"
#include "../conversation/EspIdfConversationTextTransport.h"
#include "../conversation/EspIdfConversationUuid.h"
#include "../conversation/EspIdfTurnPollTransport.h"
#include "../conversation/TurnPollSchedule.h"
#include "../conversation/TurnPollGuard.h"

namespace {

constexpr char LogTag[] = "kufr_network";
constexpr uint32_t TokenCheckPeriodMs = 1000;
constexpr uint32_t OfflineRetryMs = 30000;
constexpr uint32_t PollCheckPeriodMs = 100;
constexpr uint64_t ConversationIdleMs = 90000;

class TurnFailureGuard final {
 public:
  TurnFailureGuard(
      ConversationTurnFailureInbox& inbox, bool opening,
      const std::string& conversationId,
      const std::string& clientTurnId, uint64_t snapshotVersion)
      : inbox_(inbox),
        report_(!opening),
        conversationId_(conversationId),
        clientTurnId_(clientTurnId),
        snapshotVersion_(snapshotVersion) {}

  ~TurnFailureGuard() {
    if (report_) {
      inbox_.Store(conversationId_, clientTurnId_, snapshotVersion_);
    }
  }

  void MarkCompleted() { report_ = false; }

 private:
  ConversationTurnFailureInbox& inbox_;
  bool report_;
  const std::string& conversationId_;
  const std::string& clientTurnId_;
  uint64_t snapshotVersion_;
};

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
    DeviceIdentity& identity, ConversationRuntimeState& runtimeState,
    const std::string& bootId, EspIdfStationNetwork& station)
    : identity_(identity), runtimeState_(runtimeState), station_(station),
      settings_(station.Settings()), bootId_(bootId) {}

bool EspIdfBackendNetwork::QueueTextTurn(
    const std::string& text, const std::string& conversationId,
    const std::string& clientTurnId,
    const ConversationRuntimeSnapshot& expectedSnapshot) {
  std::string normalizedId;
  if (voiceBusy_.load() || !online_.load() || cancelRequested_.load() ||
      replyInbox_.HasPending(runtimeState_) ||
      !NormalizeConversationUuid(conversationId, normalizedId)) {
    return false;
  }
  std::lock_guard<std::mutex> presentationLock(presentationMutex_);
  if (terminalProposalOutstanding_ || presentationActive_) {
    return false;
  }
  std::lock_guard<std::mutex> lock(openingMutex_);
  if (!online_.load() || cancelRequested_.load()) {
    return false;
  }
  if (admittedSnapshotVersion_ == expectedSnapshot.version &&
      !admittedLocalConversationId_.empty() &&
      admittedLocalConversationId_ != normalizedId) {
    return false;
  }
  if (admittedSnapshotVersion_ != expectedSnapshot.version) {
    DiscardTextUnless(conversationId);
  }
  if (!textInbox_.Submit(text, conversationId, clientTurnId,
                         expectedSnapshot, runtimeState_)) {
    return false;
  }
  admittedLocalConversationId_ = normalizedId;
  admittedSnapshotVersion_ = expectedSnapshot.version;
  return true;
}

bool EspIdfBackendNetwork::QueueOpening(
    const std::string& conversationId,
    const ConversationRuntimeSnapshot& expectedSnapshot) {
  std::string normalizedId;
  if (voiceBusy_.load() || !online_.load() || cancelRequested_.load() ||
      expectedSnapshot.version == 0 ||
      replyInbox_.HasPending(runtimeState_) ||
      !NormalizeConversationUuid(conversationId, normalizedId)) {
    return false;
  }
  std::lock_guard<std::mutex> presentationLock(presentationMutex_);
  if (terminalProposalOutstanding_ || presentationActive_) {
    return false;
  }
  std::lock_guard<std::mutex> lock(openingMutex_);
  if (voiceBusy_.load() || !online_.load() || cancelRequested_.load() || openingPending_ ||
      replyInbox_.HasPending(runtimeState_) ||
      !runtimeState_.Matches(expectedSnapshot)) {
    return false;
  }
  openingSnapshotVersion_ = expectedSnapshot.version;
  openingConversationId_ = conversationId;
  openingPending_ = true;
  admittedLocalConversationId_ = normalizedId;
  admittedSnapshotVersion_ = expectedSnapshot.version;
  DiscardTextUnless(conversationId);
  return true;
}

void EspIdfBackendNetwork::QueueCancel() {
  StopVoice();
  cancelRequested_.store(true);
  textInbox_.Clear();
  replyInbox_.Clear();
  failedTurns_.Clear();
  pendingProposal_.Clear();
  std::lock_guard<std::mutex> lock(openingMutex_);
  openingPending_ = false;
  openingConversationId_.clear();
}

bool EspIdfBackendNetwork::QueueCancelFor(
    const std::string& conversationId, uint64_t snapshotVersion) {
  std::string normalizedId;
  if (!NormalizeConversationUuid(conversationId, normalizedId)) {
    return false;
  }
  std::lock_guard<std::mutex> lock(openingMutex_);
  if (admittedLocalConversationId_ != normalizedId ||
      admittedSnapshotVersion_ != snapshotVersion) {
    return false;
  }
  // Keep admission and cancellation under one lock: a delayed old socket
  // must not clear a newer owner's accepted turn.
  cancelRequested_.store(true);
  textInbox_.Clear();
  replyInbox_.Clear();
  failedTurns_.Clear();
  pendingProposal_.Clear();
  openingPending_ = false;
  openingConversationId_.clear();
  return true;
}

bool EspIdfBackendNetwork::TakeFailedTurnFor(
    const std::string& conversationId, const std::string& clientTurnId,
    uint64_t snapshotVersion) {
  return failedTurns_.TakeFor(
      conversationId, clientTurnId, snapshotVersion);
}

bool EspIdfBackendNetwork::TakeReply(
    std::string& reply, std::string& conversationId,
    std::string& clientTurnId,
    ConversationRuntimeSnapshot& snapshot, uint64_t& token) {
  if (cancelRequested_.load() ||
      !replyInbox_.Take(reply, conversationId, clientTurnId, snapshot, token,
                        runtimeState_)) {
    return false;
  }
  if (cancelRequested_.load()) {
    std::fill(reply.begin(), reply.end(), '\0');
    reply.clear();
    conversationId.clear();
    clientTurnId.clear();
    return false;
  }
  return true;
}

bool EspIdfBackendNetwork::IsReplyCurrent(
    uint64_t token, const ConversationRuntimeSnapshot& snapshot) const {
  return !cancelRequested_.load() &&
         replyInbox_.IsCurrent(token, snapshot, runtimeState_);
}

bool EspIdfBackendNetwork::FinishReply(uint64_t token) {
  return replyInbox_.Complete(token);
}

bool EspIdfBackendNetwork::ReadPendingProposal(
    TurnProposal& proposal, uint64_t nowMs) const {
  return pendingProposal_.Read(proposal, runtimeState_, nowMs);
}

bool EspIdfBackendNetwork::BeginPendingPresentation(
    WandPresentation& presentation, uint64_t nowMs) {
  std::lock_guard<std::mutex> lock(presentationMutex_);
  if (!terminalProposalOutstanding_ || presentationActive_ ||
      cancelRequested_.load() ||
      replyInbox_.HasPending(runtimeState_) ||
      !presentation.Start(pendingProposal_, runtimeState_, nowMs)) {
    return false;
  }
  presentationActive_ = true;
  return true;
}

bool EspIdfBackendNetwork::FinishPresentation(
    const WandPresentation& presentation) {
  std::lock_guard<std::mutex> lock(presentationMutex_);
  if (!presentationActive_ ||
      (presentation.State() != WandPresentationState::Idle &&
       presentation.State() != WandPresentationState::Fault)) {
    return false;
  }
  presentationActive_ = false;
  terminalProposalOutstanding_ = false;
  // Admission also takes presentationMutex_ first. Keep it closed until
  // cancellation is queued, so a new text turn cannot be cleared here.
  QueueCancel();
  return true;
}

bool EspIdfBackendNetwork::Start(bool nvsInitialized) {
  if (task_ != nullptr || !nvsInitialized || bootId_.empty() ||
      !identity_.IsProvisioned() || !settings_.IsConfigured()) {
    return false;
  }

  if (xTaskCreate(TaskEntry, "backend_network", 16384, this, 4, &task_) !=
      pdPASS) {
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
  network->FailQueuedText();
  network->task_ = nullptr;
  vTaskDelete(nullptr);
}

void EspIdfBackendNetwork::Run() {
  EspIdfStationNetwork& station = station_;

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
  startRequest.bootId = bootId_;
  bool sntpStarted = false;
  bool timeSynchronized = false;
  bool revokedLogged = false;
  uint64_t lastTurnAtMs = 0;
  while (true) {
    if (!station.HasIp()) {
      StopVoice();
      online_.store(false);
      FailQueuedText();
      pendingProposal_.Clear();
      {
        std::lock_guard<std::mutex> lock(openingMutex_);
        openingPending_ = false;
      }
      WaitMs(100);
      continue;
    }
    if (startRequest.idempotencyKey.empty()) {
      startRequest.idempotencyKey = GenerateEspIdfConversationUuid();
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
          !station.HasIp()) {
        online_.store(false);
        FailQueuedText();
        WaitMs(OfflineRetryMs);
        continue;
      }
      timeSynchronized = true;
    }

    const std::time_t currentTime = std::time(nullptr);
    if (currentTime <= 0 ||
        !trustedTime.CheckAndRecord(static_cast<uint64_t>(currentTime))) {
      online_.store(false);
      FailQueuedText();
      ESP_LOGW(LogTag, "Time not trusted; backend offline");
      WaitMs(OfflineRetryMs);
      continue;
    }

    const uint64_t nowMs = MonotonicMs();
    if (!tokens.EnsureToken(nowMs)) {
      online_.store(false);
      FailQueuedText();
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
    // A turn may have been queued while the previous poll was in progress.
    // Keep it queued until the output owner finishes or cancels that reply.
    if (replyInbox_.HasPending(runtimeState_)) {
      WaitMs(TokenCheckPeriodMs);
      continue;
    }
    if (voicePending_.exchange(false)) {
      RunVoice(tokens, coordinator, startRequest);
      lastTurnAtMs = MonotonicMs();
      continue;
    }
    bool opening = false;
    std::string openingConversationId;
    uint64_t openingVersion = 0;
    {
      std::lock_guard<std::mutex> lock(openingMutex_);
      if (openingPending_) {
        opening = true;
        openingConversationId = std::move(openingConversationId_);
        openingVersion = openingSnapshotVersion_;
        openingPending_ = false;
        openingConversationId_.clear();
      }
    }
    std::string text;
    std::string conversationId;
    std::string clientTurnId;
    uint64_t snapshotVersion = 0;
    if (opening) {
      RunTurn("", openingConversationId, "", true, openingVersion,
              tokens, station, coordinator,
              startRequest);
      lastTurnAtMs = MonotonicMs();
    } else if (textInbox_.Take(
                   text, conversationId, clientTurnId, snapshotVersion)) {
      RunTurn(text, conversationId, clientTurnId, false, snapshotVersion,
              tokens, station,
              coordinator, startRequest);
      lastTurnAtMs = MonotonicMs();
      std::fill(text.begin(), text.end(), '\0');
      text.clear();
    }
    WaitMs(TokenCheckPeriodMs);
  }
}

void EspIdfBackendNetwork::RunTurn(
    const std::string& text, const std::string& conversationId,
    const std::string& clientTurnId,
    bool opening, uint64_t snapshotVersion,
    DeviceTokenSession& tokens,
    EspIdfStationNetwork& station,
    ConversationStartCoordinator& coordinator,
    ConversationStartRequest& startRequest) {
  TurnFailureGuard failureGuard(
      failedTurns_, opening, conversationId, clientTurnId,
      snapshotVersion);
  std::string backendClientTurnId;
  std::string normalizedConversationId;
  if (!NormalizeConversationUuid(conversationId, normalizedConversationId) ||
      (!opening &&
       !NormalizeConversationUuid(clientTurnId, backendClientTurnId))) {
    return;
  }
  ConversationRuntimeSnapshot snapshot;
  if (!station.HasIp() || !runtimeState_.Read(snapshot) ||
      snapshot.version != snapshotVersion || bootId_.empty()) {
    ESP_LOGW(LogTag, "Text turn dropped: device state unavailable");
    return;
  }
  if (!IsLocalConversationAdmitted(
          normalizedConversationId, snapshotVersion)) {
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
  if (!opening && !activeLocalConversationId_.empty() &&
      activeLocalConversationId_ != normalizedConversationId) {
    ESP_LOGW(LogTag, "Text turn dropped: local conversation changed");
    return;
  }
  if (opening && !coordinator.ConversationId().empty()) {
    EspIdfConversationTextTransport closeTransport(settings_.BackendHost());
    if (!CloseConversation(coordinator, closeTransport, tokens,
                           startRequest)) {
      return;
    }
  }
  startRequest.audioConsent = false;
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
  activeLocalConversationId_ = normalizedConversationId;
  if (!runtimeState_.Matches(snapshot)) {
    return;
  }

  ConversationTextTurnRequest turnRequest;
  turnRequest.conversationId = coordinator.ConversationId();
  turnRequest.clientTurnId = opening
      ? GenerateEspIdfConversationUuid()
      : backendClientTurnId;
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
      station.HasIp() && runtimeState_.Matches(snapshot) &&
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
      activeLocalConversationId_.clear();
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
  while (station.HasIp() && schedule.IsActive() &&
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
        !schedule.IsExpired(completedAtMs) && station.HasIp() &&
        !cancelRequested_.load() &&
        runtimeState_.Matches(snapshot)) {
      if (result.decision == TurnPollDecision::ProposalReady) {
        if (!StoreReplyIfAdmitted(
                result.reply,
                conversationId,
                opening ? turnRequest.clientTurnId : clientTurnId,
                snapshot)) {
          std::fill(result.reply.begin(), result.reply.end(), '\0');
          CloseConversation(coordinator, textTransport, tokens, startRequest);
          break;
        }
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
          FailQueuedText();
          {
            std::lock_guard<std::mutex> openingLock(openingMutex_);
            openingPending_ = false;
          }
          ESP_LOGI(LogTag, "Validated proposal held for presentation owner");
        } else {
          replyInbox_.Clear();
          CloseConversation(coordinator, textTransport, tokens, startRequest);
        }
      } else {
        pendingProposal_.Clear();
        if (!StoreReplyIfAdmitted(
                result.reply,
                conversationId,
                opening ? turnRequest.clientTurnId : clientTurnId,
                snapshot)) {
          std::fill(result.reply.begin(), result.reply.end(), '\0');
          CloseConversation(coordinator, textTransport, tokens, startRequest);
          break;
        }
      }
      if (cancelRequested_.load()) {
        replyInbox_.Clear();
        std::fill(result.reply.begin(), result.reply.end(), '\0');
        break;
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
  if (replyConsumed) {
    failureGuard.MarkCompleted();
  }
  if (!replyConsumed || noSelection) {
    CloseConversation(coordinator, textTransport, tokens, startRequest);
  }
}

void EspIdfBackendNetwork::FailQueuedText() {
  std::lock_guard<std::mutex> lock(openingMutex_);
  std::string text;
  std::string conversationId;
  std::string clientTurnId;
  uint64_t snapshotVersion = 0;
  if (textInbox_.Take(
          text, conversationId, clientTurnId, snapshotVersion)) {
    std::fill(text.begin(), text.end(), '\0');
    failedTurns_.Store(
        conversationId, clientTurnId, snapshotVersion);
  }
}

void EspIdfBackendNetwork::DiscardTextUnless(
    const std::string& conversationId) {
  // The caller holds openingMutex_ while changing the admitted local session.
  std::string discardedConversationId;
  std::string discardedTurnId;
  uint64_t discardedVersion = 0;
  if (textInbox_.DiscardUnless(
          conversationId, &discardedConversationId,
          &discardedTurnId, &discardedVersion)) {
    failedTurns_.Store(
        discardedConversationId, discardedTurnId, discardedVersion);
  }
}

bool EspIdfBackendNetwork::IsLocalConversationAdmitted(
    const std::string& normalizedConversationId, uint64_t snapshotVersion) {
  std::lock_guard<std::mutex> lock(openingMutex_);
  return admittedSnapshotVersion_ == snapshotVersion &&
         admittedLocalConversationId_ == normalizedConversationId;
}

bool EspIdfBackendNetwork::StoreReplyIfAdmitted(
    const std::string& reply, const std::string& conversationId,
    const std::string& clientTurnId,
    const ConversationRuntimeSnapshot& snapshot) {
  std::string normalizedId;
  if (!NormalizeConversationUuid(conversationId, normalizedId)) {
    return false;
  }
  std::lock_guard<std::mutex> lock(openingMutex_);
  return admittedSnapshotVersion_ == snapshot.version &&
         admittedLocalConversationId_ == normalizedId &&
         replyInbox_.Store(
             reply, conversationId, clientTurnId, snapshot, runtimeState_);
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
  activeLocalConversationId_.clear();
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

bool EspIdfBackendNetwork::InitializeVoice(bool nvsInitialized) {
  const bool available = voiceAudio_.Initialize(nvsInitialized);
  std::lock_guard<std::mutex> lock(voiceMutex_);
  voiceStatus_.available = available;
  voiceStatus_.phase = available ? "idle" : "unavailable";
  return available;
}

LocalVoiceStatus EspIdfBackendNetwork::VoiceStatus() const {
  std::lock_guard<std::mutex> lock(voiceMutex_);
  return voiceStatus_;
}

bool EspIdfBackendNetwork::ConfigureVoice(const std::string& mode, const std::string& voice) {
  if ((mode != "off" && mode != "realtime") ||
      (voice != "cedar" && voice != "marin" && voice != "coral")) return false;
  std::lock_guard<std::mutex> lock(voiceMutex_);
  if (voiceBusy_.load() || (mode == "realtime" && !voiceStatus_.available)) return false;
  voiceStatus_.mode = mode;
  voiceStatus_.voice = voice;
  voiceStatus_.error.clear();
  voiceStatus_.phase = voiceStatus_.available ? "idle" : "unavailable";
  return true;
}

bool EspIdfBackendNetwork::StartVoice(const std::string& requestId,
                                     const ConversationRuntimeSnapshot& snapshot) {
  if (!online_.load() || cancelRequested_.load() || !IsConversationUuid(requestId) ||
      !runtimeState_.Matches(snapshot) || replyInbox_.HasPending(runtimeState_)) return false;
  std::lock_guard<std::mutex> presentationLock(presentationMutex_);
  std::lock_guard<std::mutex> openingLock(openingMutex_);
  std::lock_guard<std::mutex> voiceLock(voiceMutex_);
  if (terminalProposalOutstanding_ || presentationActive_ || openingPending_ || voiceBusy_.load() ||
      requestId == lastVoiceRequestId_ || voiceStatus_.mode != "realtime" || !voiceStatus_.available) return false;
  voiceSnapshot_ = snapshot;
  voiceRequestId_ = lastVoiceRequestId_ = requestId;
  voiceStatus_.phase = "starting";
  voiceStatus_.error.clear();
  voiceStartEpoch_ = voiceEpoch_.fetch_add(1) + 1;
  voiceQueuedAtMs_ = MonotonicMs();
  voiceBusy_.store(true);
  voicePending_.store(true);
  return true;
}

void EspIdfBackendNetwork::StopVoice() {
  voiceEpoch_.fetch_add(1);
  const bool pending = voicePending_.exchange(false);
  if (pending) voiceBusy_.store(false);
  voiceAudio_.Stop();
  std::lock_guard<std::mutex> lock(voiceMutex_);
  if (voiceBusy_.load()) voiceStatus_.phase = "stopping";
  if (!voiceBusy_.load()) voiceStatus_.phase = voiceStatus_.available ? "idle" : "unavailable";
}

void EspIdfBackendNetwork::SetVoicePhase(const char* phase) {
  std::lock_guard<std::mutex> lock(voiceMutex_);
  voiceStatus_.phase = phase;
}

void EspIdfBackendNetwork::RunVoice(DeviceTokenSession& tokens,
                                    ConversationStartCoordinator& coordinator,
                                    ConversationStartRequest& startRequest) {
  ConversationRuntimeSnapshot snapshot;
  std::string requestId, voice;
  uint64_t epoch = 0, queuedAtMs = 0;
  {
    std::lock_guard<std::mutex> lock(voiceMutex_);
    snapshot = voiceSnapshot_; requestId = voiceRequestId_; voice = voiceStatus_.voice;
    epoch = voiceStartEpoch_; queuedAtMs = voiceQueuedAtMs_;
  }
  const auto current = [this, snapshot, epoch]() {
    return voiceEpoch_.load() == epoch && !cancelRequested_.load() &&
           station_.HasIp() && runtimeState_.Matches(snapshot);
  };
  voiceAudio_.Begin();
  EspIdfConversationTextTransport cleanup(settings_.BackendHost());
  bool succeeded = false;
  TurnOperationObservation final;
  std::string error = "ai_unavailable";
  if (MonotonicMs() - queuedAtMs <= 5000 && current() && CloseConversation(coordinator, cleanup, tokens, startRequest)) {
    startRequest.audioConsent = true;
    startRequest.generation = snapshot.generation;
    startRequest.catalogVersion = snapshot.catalogVersion;
    startRequest.idempotencyKey = requestId;
    const auto started = coordinator.Start(startRequest, MonotonicMs());
    if ((started == ConversationStartOutcome::Started || started == ConversationStartOutcome::AlreadyStarted) && current()) {
      TurnCorrelation correlation;
      correlation.deviceId = identity_.DeviceId();
      correlation.bootId = bootId_;
      correlation.conversationId = coordinator.ConversationId();
      correlation.clientTurnId = GenerateEspIdfConversationUuid();
      correlation.generation = snapshot.generation;
      correlation.deviceRevision = snapshot.deviceRevision;
      const std::string body = "{\"clientTurnId\":\"" + correlation.clientTurnId +
          "\",\"bootId\":\"" + bootId_ + "\",\"generation\":" + std::to_string(snapshot.generation) +
          ",\"readyMask\":" + std::to_string(snapshot.readyMask) +
          ",\"deviceRevision\":" + std::to_string(snapshot.deviceRevision) +
          ",\"catalogVersion\":\"" + snapshot.catalogVersion +
          "\",\"opening\":true,\"text\":\"\",\"wav\":null,\"voice\":\"" + voice + "\",\"speed\":1}";
      const char* token = tokens.AccessToken(MonotonicMs());
      EspIdfRealtimeTransport transport(settings_.BackendHost(), token ? token : "", voiceAudio_,
          current, [this](const char* phase) { SetVoicePhase(phase); });
      succeeded = transport.Run(body, GenerateEspIdfConversationUuid(), correlation, snapshot,
          [&final, &current](const TurnOperationObservation& operation) {
            if (!current()) return false;
            final = operation; return true;
          });
      if (!transport.Error().empty()) error = transport.Error();
    }
  }
  voiceAudio_.Stop();
  bool proposalStored = false;
  if (succeeded && current() && final.hasProposal) {
    const auto utc = std::time(nullptr);
    const uint64_t remaining = utc > 0 ? ProposalRemainingMs(final.proposal, static_cast<uint64_t>(utc)) : 0;
    proposalStored = remaining && pendingProposal_.Store(final.proposal, snapshot, runtimeState_, MonotonicMs(), remaining);
    if (proposalStored) {
      std::lock_guard<std::mutex> lock(presentationMutex_);
      terminalProposalOutstanding_ = true;
    }
  }
  if (!proposalStored) CloseConversation(coordinator, cleanup, tokens, startRequest);
  startRequest.audioConsent = false;
  {
    std::lock_guard<std::mutex> lock(voiceMutex_);
    voiceStatus_.phase = succeeded && current() ? "completed" : voiceEpoch_.load() != epoch ? "idle" : "failed";
    voiceStatus_.error = voiceStatus_.phase == "failed" ? error : "";
    voiceBusy_.store(false);
  }
  std::fill(final.reply.begin(), final.reply.end(), '\0');
}

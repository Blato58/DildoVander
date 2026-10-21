#include "../voice_turn/VoiceWorkflow.h"
#include "EspIdfBackendNetwork.h"

#include <algorithm>
#include <ctime>
#include <utility>

#include "esp_log.h"
#include "esp_netif_sntp.h"
#include "esp_random.h"
#include "esp_timer.h"

#include "BuildDate.h"
#include "../voice_turn/EspIdfDeviceMediaTransport.h"
#include "../voice_turn/VoiceOutputCancellation.h"
#include "../voice_turn/VoiceTestContext.h"
#include "../provisioning/EspIdfSha256.h"
#include "DeviceTokenSession.h"
#include "EspIdfStationNetwork.h"
#include "EspIdfTokenExchangeTransport.h"
#include "EspIdfTrustedTimeStore.h"
#include "TrustedTime.h"
#include "../conversation/ConversationStartCoordinator.h"
#include "../conversation/ConversationStartCodec.h"
#include "../conversation/EspIdfConversationStartTransport.h"
#include "../conversation/EspIdfConversationCleanup.h"
#include "../conversation/EspIdfConversationUuid.h"
#include "../conversation/TurnPollGuard.h"
#include "../costume_photo/EspIdfCostumeUpload.h"
#include "../show_cues/EspIdfDeviceShowTransport.h"

namespace {

constexpr char LogTag[] = "kufr_network";
constexpr uint32_t TokenCheckPeriodMs = 1000;
constexpr uint32_t OfflineRetryMs = 30000;
constexpr uint64_t ConversationIdleMs = 90000;

uint64_t MonotonicMs() {
  return static_cast<uint64_t>(esp_timer_get_time() / 1000);
}

void WaitMs(uint32_t durationMs) {
  vTaskDelay(pdMS_TO_TICKS(durationMs));
}

}  // namespace

EspIdfBackendNetwork::EspIdfBackendNetwork(
    DeviceIdentity& identity, ConversationRuntimeState& runtimeState,
    const std::string& bootId, EspIdfStationNetwork& station,
    const CommandJournal& ownerJournal, JournalStorage& journalStorage)
    : identity_(identity), runtimeState_(runtimeState), station_(station),
      settings_(station.Settings()), keepsake_(identity, station, online_, ownerJournal, journalStorage), bootId_(bootId) {}

EspIdfBackendNetwork::EspIdfBackendNetwork(DeviceIdentity& identity, ConversationRuntimeState& runtimeState,
    const std::string& bootId, EspIdfStationNetwork& station, DeviceStorageRuntime& storage)
    : identity_(identity), runtimeState_(runtimeState), station_(station), settings_(station.Settings()),
      keepsake_(identity, station, online_, storage), bootId_(bootId) {}

uint32_t EspIdfBackendNetwork::AuthenticationState() const {
  const auto state = authState_.load();
  return state == 1 && MonotonicMs() >= authExpiresAtMs_.load() ? 2 : state;
}

bool EspIdfBackendNetwork::ReadVoiceMediaHealth(VoiceMediaHealth& health) const {
  std::unique_lock<std::mutex> lock(mediaHealthMutex_, std::try_to_lock);
  if (!lock) return false;
  health = mediaHealth_;
  health.cancellationEpoch = voiceEpoch_.load();
  return true;
}

void EspIdfBackendNetwork::PublishVoiceMediaHealth(const VoiceMediaHealth& health) {
  std::unique_lock<std::mutex> lock(mediaHealthMutex_, std::try_to_lock);
  if (lock) mediaHealth_ = health;
}

void EspIdfBackendNetwork::QueueCancel() {
  std::lock_guard<std::mutex> lock(presentationMutex_);
  QueueCancelLocked();
}

void EspIdfBackendNetwork::RetireCandidateLocked(const char* reason) {
  presentationClaimed_ = false;
  if (keepsakeCandidate_.outcomeId.empty()) return;
  if (technicalCandidate_) { ClearCandidateLocked(); return; }
  if (keepsake_.UsesSharedStorage() && keepsakeHandle_.generation) {
    if (!keepsakeRetirementReason_.front()) {
      size_t length = 0;
      while (length < keepsakeRetirementReason_.size() && reason[length]) ++length;
      if (length == keepsakeRetirementReason_.size()) return;
      std::copy(reason, reason + length, keepsakeRetirementReason_.begin());
    }
    if (!keepsake_.RetireOwned(keepsakeHandle_, keepsakeRetirementReason_.data())) return;
  } else if (!keepsake_.UsesSharedStorage()) keepsake_.Retire(keepsakeCandidate_, reason);
  ClearCandidateLocked();
}

void EspIdfBackendNetwork::ClearCandidateLocked() {
  keepsakeCandidate_ = {};
  keepsakeCandidatePrepared_ = false;
  technicalCandidate_ = false;
  keepsakeHandle_ = {};
  keepsakeRetirementReason_ = {};
}

void EspIdfBackendNetwork::QueueCancelLocked() {
  if (!presentationActive_) RetireCandidateLocked("proposal_cancelled");
  StopVoiceOutput();
  cancelRequested_.store(true);
  pendingProposal_.Clear();
}

bool EspIdfBackendNetwork::ReadPendingProposal(
    TurnProposal& proposal, uint64_t nowMs) const {
  return pendingProposal_.Read(proposal, runtimeState_, nowMs);
}

bool EspIdfBackendNetwork::BeginPendingPresentation(
    WandPresentation& presentation, uint64_t nowMs) {
  std::lock_guard<std::mutex> lock(presentationMutex_);
  if (keepsakeRetirementReason_.front()) {
    presentation.CancelPendingReservation();
    RetireCandidateLocked(keepsakeRetirementReason_.data());
    return false;
  }
  if ((!terminalProposalOutstanding_ || cancelRequested_.load()) &&
      presentation.State() == WandPresentationState::AwaitingAdmission) {
    presentation.CancelPendingReservation();
    RetireCandidateLocked("proposal_cancelled");
    pendingProposal_.Clear();
    terminalProposalOutstanding_ = false;
    return false;
  }
  if (!terminalProposalOutstanding_ || presentationActive_ || cancelRequested_.load()) {
    return false;
  }
  TurnProposal proposal;
  const bool claimed = presentation.ReadClaimedProposal(proposal);
  if ((claimed || pendingProposal_.Read(proposal, runtimeState_, nowMs)) && proposal.operationEpoch != 0) {
    // This is the single control owner. The mutex retains the original intent;
    // no variant selection may interleave between fingerprint capture and Start.
    const bool candidateMatches = !keepsakeCandidate_.outcomeId.empty() &&
        keepsakeCandidate_.operationEpoch == proposal.operationEpoch &&
        keepsakeCandidate_.physicalOperationId == proposal.proposalId &&
        keepsakeCandidate_.compartment == proposal.compartment;
    if (candidateMatches && keepsakeCandidate_.physicalFingerprint == 0)
      keepsakeCandidate_.physicalFingerprint = claimed ? presentation.ClaimedOperationFingerprint()
          : presentation.CurrentOperationFingerprint();
    const bool reserved = candidateMatches && keepsakeCandidate_.physicalFingerprint != 0 &&
        (!claimed || keepsakeCandidate_.physicalFingerprint == presentation.ClaimedOperationFingerprint()) &&
        presentation.ReservePendingOperation(pendingProposal_, runtimeState_, nowMs);
    presentationClaimed_ = presentation.State() == WandPresentationState::AwaitingAdmission;
    if (!reserved && presentation.AdmissionIsPending()) return false;
    bool prepared = keepsakeCandidatePrepared_;
    if (reserved && proposal.technicalTest && technicalCandidate_) prepared = true;
    if (reserved && !prepared) {
      if (keepsake_.UsesSharedStorage()) {
        const auto result = keepsake_.PrepareOwned(keepsakeCandidate_, keepsakeHandle_);
        if (result == EspIdfKeepsakeNotifier::Preparation::Pending) return false;
        prepared = result == EspIdfKeepsakeNotifier::Preparation::Ready;
      } else prepared = keepsake_.Prepare(keepsakeCandidate_);
    }
    if (!reserved || !prepared) {
      presentation.CancelPendingReservation();
      RetireCandidateLocked("proposal_start_rejected");
      pendingProposal_.Clear();
      terminalProposalOutstanding_ = false;
      return false;
    }
    keepsakeCandidatePrepared_ = true;
  }
  // Scoped preparation may persist flash; recheck its original TTL after I/O.
  const uint64_t admissionAtMs = proposal.operationEpoch != 0 ? MonotonicMs() : nowMs;
  const bool started = !cancelRequested_.load() &&
      presentation.StartPolled(pendingProposal_, runtimeState_, admissionAtMs);
  presentationClaimed_ = presentation.State() == WandPresentationState::AwaitingAdmission;
  if (!started && !cancelRequested_.load() && presentation.AdmissionIsPending()) return false;
  if (!started) {
    presentation.CancelPendingReservation();
    RetireCandidateLocked("proposal_start_rejected");
    pendingProposal_.Clear();
    terminalProposalOutstanding_ = false;
    return false;
  }
  presentationActive_ = true;
  presentationClaimed_ = false;
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
  if (technicalCandidate_) {
    if (presentation.State() == WandPresentationState::Idle &&
        !keepsake_.QueueTechnical(keepsakeCandidate_)) return false;
    // Its durable journal origin/outcome remains even if a reset loses this
    // volatile technical report. Never persist it in the production outbox.
    ClearCandidateLocked();
  }
  if (keepsake_.UsesSharedStorage() && !keepsakeCandidate_.outcomeId.empty()) {
    if (presentation.State() == WandPresentationState::Idle) {
      if (!keepsake_.CompleteOwned(keepsakeHandle_)) return false;
      ClearCandidateLocked();
    } else {
      RetireCandidateLocked("presentation_fault");
      if (!keepsakeCandidate_.outcomeId.empty()) return false;
    }
  }
  presentationActive_ = false;
  terminalProposalOutstanding_ = false;
  const auto completedOutcome = keepsakeCandidate_;
  ClearCandidateLocked();
  // Admission also takes presentationMutex_ first. Keep it closed until
  // cancellation is queued, so a new text turn cannot be cleared here.
  QueueCancelLocked();
  if (presentation.State() == WandPresentationState::Idle && !completedOutcome.operationId.empty()) {
    // No HTTP or flash writes on the control owner. Background persistence precedes send.
    if (keepsake_.Queue(completedOutcome) == KeepsakeQueueResult::Rejected) {
      ESP_LOGW(LogTag, "Keepsake completed-outcome outbox unavailable");
      keepsake_.Retire(completedOutcome, "completion_unconfirmed");
    }
  } else if (!completedOutcome.outcomeId.empty()) {
    keepsake_.Retire(completedOutcome, "presentation_fault");
  }
  return true;
}

bool EspIdfBackendNetwork::Start(bool nvsInitialized) {
  if (task_ != nullptr || !nvsInitialized || bootId_.empty() ||
      !identity_.IsProvisioned() || !settings_.IsConfigured()) {
    return false;
  }
  keepsake_.Start(nvsInitialized);

  if (xTaskCreate(TaskEntry, "backend_network", 16384, this, 4, &task_) !=
      pdPASS) {
    task_ = nullptr;
    return false;
  }
  // HTTPS/JSON only: no audio frame or device-token exchange lives here. The
  // independent bounded stack keeps Live's blocking workflow from hiding Stop
  // settings or non-audio scene cues. Existing audio worker remains unchanged.
  if (xTaskCreate(ShowTaskEntry, "show_poll", 8192, this, 3, &showTask_) != pdPASS) {
    showTask_ = nullptr;
    ESP_LOGE(LogTag, "Show polling unavailable: task allocation failed");
  }
  return true;
}

void EspIdfBackendNetwork::ShowTaskEntry(void* argument) {
  static_cast<EspIdfBackendNetwork*>(argument)->RunShowCues();
}
void EspIdfBackendNetwork::RunShowCues() {
  for (;;) {
    auto retired = showCues_.TakeRetiredAudio(); retired.reset();
    BorrowedShowToken::Copy token;
    if (online_.load() && station_.HasIp() && showToken_.Read(MonotonicMs(), token))
      PumpShowCues(token.bytes.data());
    WaitMs(TokenCheckPeriodMs);
  }
}

void EspIdfBackendNetwork::TaskEntry(void* argument) {
  EspIdfBackendNetwork* network =
      static_cast<EspIdfBackendNetwork*>(argument);
  network->Run();
  network->online_.store(false);
  network->showToken_.Clear();
  network->task_ = nullptr;
  vTaskDelete(nullptr);
}

void EspIdfBackendNetwork::Run() {
  EspIdfStationNetwork& station = station_;

  EspIdfTrustedTimeStore timeStore(true);
  const uint64_t buildDate = ParseBuildDateUtc(__DATE__);
  TrustedTime trustedTime(timeStore, buildDate);
  if (buildDate == 0 || !trustedTime.IsHealthy()) {
    timeState_.store(3);
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
    // Local expiry must retire preparation even when WAN/time/token checks fail.
    bool proposalExpired = false;
    {
      std::lock_guard<std::mutex> lock(presentationMutex_);
      if (keepsakeRetirementReason_.front()) RetireCandidateLocked(keepsakeRetirementReason_.data());
      TurnProposal candidate;
      proposalExpired = terminalProposalOutstanding_ && !presentationActive_ && !presentationClaimed_
          && !pendingProposal_.Read(candidate, runtimeState_, MonotonicMs());
      if (proposalExpired) {
        RetireCandidateLocked("proposal_stale_or_expired");
        pendingProposal_.Clear();
        terminalProposalOutstanding_ = false;
      }
    }
    if (!station.HasIp()) {
      StopVoice();
      online_.store(false);
      showToken_.Clear();
      pendingProposal_.Clear();
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
      timeState_.store(1);
      if (esp_netif_sntp_sync_wait(pdMS_TO_TICKS(10000)) != ESP_OK ||
          !station.HasIp()) {
        online_.store(false);
      showToken_.Clear();
          WaitMs(OfflineRetryMs);
        continue;
      }
      timeSynchronized = true;
    }

    const std::time_t currentTime = std::time(nullptr);
    if (currentTime <= 0 ||
        !trustedTime.CheckAndRecord(static_cast<uint64_t>(currentTime))) {
      timeState_.store(3);
      online_.store(false);
      showToken_.Clear();
      ESP_LOGW(LogTag, "Time not trusted; backend offline");
      WaitMs(OfflineRetryMs);
      continue;
    }

    timeState_.store(2);

    const uint64_t nowMs = MonotonicMs();
    if (showUnauthorized_.exchange(false)) tokens.HandleUnauthorized(nowMs);
    if (!tokens.EnsureToken(nowMs)) {
      authState_.store(tokens.IsRevoked() ? 3 : 4);
      online_.store(false);
      showToken_.Clear();
      if (tokens.IsRevoked() && !revokedLogged) {
        ESP_LOGE(LogTag, "Device credential rejected; backend offline");
        revokedLogged = true;
      }
      WaitMs(OfflineRetryMs);
      continue;
    }
    showToken_.Publish(tokens.AccessToken(nowMs), tokens.AccessTokenExpiresAtMs());
    authExpiresAtMs_.store(tokens.AccessTokenExpiresAtMs());
    authState_.store(1);
    online_.store(true);
    EspIdfConversationCleanup sessionTransport(settings_.BackendHost());
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
    // Only an explicit supported voice start enters the provider workflow.
    if (voicePending_.exchange(false)) {
      RunVoice(tokens, coordinator, startRequest);
      lastTurnAtMs = MonotonicMs();
      continue;
    }
    WaitMs(TokenCheckPeriodMs);
  }
}

void EspIdfBackendNetwork::PumpShowCues(const char* token) {
  EspIdfDeviceShowTransport transport(settings_.BackendHost());
  DeviceShowAck ack;
  // A failed ACK keeps its position. At most four entries can accumulate, and
  // admission stops before the terminal result would lose its reserved slot.
  for (unsigned attempt = 0; attempt < 4 && showCues_.ReadAck(ack); ++attempt) {
    const int status = transport.Ack(ack, token);
    if (status == 200 || status == 204 || status == 400 || status == 404 || status == 409 || status == 410) {
      if (status >= 400 && ack.outcome == "accepted") showCues_.Cancel(ack.cue.cueId);
      showCues_.ConfirmAck(ack.cue.cueId, ack.outcome);
    }
    else {
      if (status == 401) { showToken_.Clear(); showUnauthorized_.store(true); return; }
      break;
    }
  }
  DeviceShowSettings applied;
  uint64_t generation = 0, revision = 0;
  const bool allowed = showCues_.PollState(applied, generation, revision);
  const uint64_t requestedAtMs = MonotonicMs();
  DeviceShowEnvelope envelope;
  const int status = transport.Poll(bootId_, generation, revision, requestedAtMs,
      applied, VoiceStatus().voice, token, envelope);
  if (status == 401) { showToken_.Clear(); showUnauthorized_.store(true); return; }
  if (status == 200) {
    showCues_.Settings(envelope.settings);
    if (!envelope.cancelledCueId.empty()) showCues_.Cancel(envelope.cancelledCueId);
    if (allowed && !envelope.cue.cueId.empty() && envelope.cue.bootId == bootId_ &&
        envelope.cue.generation == generation && envelope.cue.deviceRevision == revision &&
        envelope.cue.issuedAtMs == requestedAtMs && !showCues_.IsActive()) {
      auto audio = envelope.cue.audioPath.empty() ? std::shared_ptr<DeviceShowAudio>{} : EspIdfDeviceShowTransport::AllocateAudio();
      showCues_.Offer(envelope.cue, std::move(audio));
    }
  }
  DeviceShowCue audioCue;
  showCues_.TakeAudio(audioCue);
  const auto view = showCues_.Read();
  if (view.stage == DeviceShowMailbox::Stage::Playing && view.audio &&
      showCues_.PlaybackCurrent(view.cue.cueId, MonotonicMs())) {
    const int downloaded = transport.Download(view.cue, token, *view.audio, showCues_);
    if (downloaded == 401) { showToken_.Clear(); showUnauthorized_.store(true); }
    else if (downloaded != 202) showCues_.AudioFinished(view.cue.cueId, downloaded == 200,
        downloaded == 200 ? "" : "download_rejected");
  }
}

bool EspIdfBackendNetwork::CloseConversation(
    ConversationStartCoordinator& coordinator,
    EspIdfConversationCleanup& transport,
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
    if (!presentationActive_) RetireCandidateLocked("conversation_closed");
    terminalProposalOutstanding_ = false;
  }
  startRequest.idempotencyKey = GenerateEspIdfConversationUuid();
  deleteIdempotencyKey_.clear();
  closePending_ = false;
  closeRetryAtMs_ = 0;
  closeRetryDelayMs_ = 1000;
  return true;
}

bool EspIdfBackendNetwork::InitializeVoice(bool nvsInitialized, AudioBoardPort* board) {
  const bool available = voiceAudio_.Initialize(nvsInitialized, board);
  std::lock_guard<std::mutex> lock(voiceMutex_);
  voiceStatus_.available = available;
  voiceStatus_.phase = available ? "idle" : "unavailable";
  return available;
}

void EspIdfBackendNetwork::InitializeCamera(bool nvsInitialized, bool profileBVerified) {
  camera_.Configure(nvsInitialized, profileBVerified);
  std::lock_guard<std::mutex> lock(voiceMutex_);
  voiceStatus_.cameraAvailable = camera_.Available();
}

LocalVoiceStatus EspIdfBackendNetwork::VoiceStatus() const {
  std::lock_guard<std::mutex> lock(voiceMutex_);
  return voiceStatus_;
}

bool EspIdfBackendNetwork::ConfigureVoice(const std::string& mode, const std::string& voice) {
  if ((mode != "off" && mode != "realtime" && mode != "gpt-live") ||
      (voice != "cedar" && voice != "marin" && voice != "coral")) return false;
  std::lock_guard<std::mutex> lock(voiceMutex_);
  if (voiceBusy_.load() || showCues_.IsActive() || (mode != "off" && !voiceStatus_.available)) return false;
  voiceStatus_.mode = mode;
  voiceStatus_.voice = voice;
  voiceStatus_.error.clear();
  voiceStatus_.phase = voiceStatus_.available ? "idle" : "unavailable";
  return true;
}

bool EspIdfBackendNetwork::StartVoice(const std::string& requestId,
                                     const ConversationRuntimeSnapshot& snapshot) {
  return StartVoiceWithImageConsent(requestId, snapshot, false);
}

bool EspIdfBackendNetwork::StartVoiceWithImageConsent(const std::string& requestId,
    const ConversationRuntimeSnapshot& snapshot, bool imageConsent) {
  return StartVoiceWithTestContext(requestId, snapshot, imageConsent, "");
}

bool EspIdfBackendNetwork::StartVoiceWithTestContext(const std::string& requestId,
    const ConversationRuntimeSnapshot& snapshot, bool imageConsent, const std::string& testContext) {
  std::string testContextId;
  if (!testContext.empty()) {
    if (!SupportsVoiceTestContext() || !ValidVoiceTestContext(testContext) || imageConsent ||
        !snapshot.operationEpoch || !snapshot.readyMask)
      return false;
    EspIdfSha256 hash;
    std::array<uint8_t, 32> digest{};
    if (!hash.Hash(reinterpret_cast<const uint8_t*>(testContext.data()), testContext.size(), digest)) return false;
    constexpr char hex[] = "0123456789abcdef";
    for (uint8_t byte : digest) { testContextId += hex[byte >> 4]; testContextId += hex[byte & 15]; }
  }
  if (!online_.load() || cancelRequested_.load() || !IsConversationUuid(requestId) ||
      !runtimeState_.Matches(snapshot)) return false;
  std::lock_guard<std::mutex> presentationLock(presentationMutex_);
  std::lock_guard<std::mutex> voiceLock(voiceMutex_);
  if (terminalProposalOutstanding_ || presentationActive_ || keepsakeRetirementReason_.front()
      || voiceBusy_.load() || showCues_.IsActive() || keepsake_.HasTechnicalDelivery() ||
      requestId == lastVoiceRequestId_ || voiceStatus_.mode == "off" || !voiceStatus_.available) return false;
  voiceSnapshot_ = snapshot;
  voiceRequestId_ = lastVoiceRequestId_ = requestId;
  voiceStatus_.phase = "starting";
  voiceStatus_.error.clear();
  voiceStatus_.photoWarning.clear();
  voiceImageConsent_ = imageConsent;
  ClearVoiceTestContext(voiceTestContext_);
  voiceTestContext_ = testContext;
  voiceStatus_.testContextId = testContextId;
  voiceStartEpoch_ = voiceEpoch_.fetch_add(1) + 1;
  voiceQueuedAtMs_ = MonotonicMs();
  voiceBusy_.store(true);
  voicePending_.store(true);
  return true;
}

bool EspIdfBackendNetwork::StopVoiceForTestContext(const std::string& testContextId) {
  if (!ValidVoiceTestContextId(testContextId)) return false;
  std::lock_guard<std::mutex> presentationLock(presentationMutex_);
  {
    std::lock_guard<std::mutex> voiceLock(voiceMutex_);
    const bool activeVoice = voiceBusy_.load() && voiceEpoch_.load() == voiceStartEpoch_;
    const bool activePhysical = technicalCandidate_ &&
        (terminalProposalOutstanding_ || presentationActive_ || presentationClaimed_);
    if (!MatchesActiveVoiceTestContext(voiceStatus_.testContextId, testContextId, activeVoice, activePhysical)) return false;
    // Burn the scoped authority before releasing either lock; a later voice
    // start cannot inherit this accepted Stop or its cancellation epoch.
    voiceStatus_.testContextId.clear();
    ClearVoiceTestContext(voiceTestContext_);
    CancelVoiceOutput(voiceEpoch_, voicePending_, voiceBusy_, voiceAudio_);
    voiceStatus_.phase = voiceBusy_.load() ? "stopping" : "idle";
  }
  if (!presentationActive_) {
    RetireCandidateLocked("proposal_stopped");
    pendingProposal_.Clear();
    terminalProposalOutstanding_ = false;
  }
  return true;
}

void EspIdfBackendNetwork::StopVoice() {
  showCues_.Cancel();
  {
    std::lock_guard<std::mutex> lock(presentationMutex_);
    if (!presentationActive_) {
      RetireCandidateLocked("proposal_stopped");
      pendingProposal_.Clear();
      terminalProposalOutstanding_ = false;
    }
  }
  StopVoiceOutput();
}

void EspIdfBackendNetwork::StopVoiceOutput() {
  std::lock_guard<std::mutex> lock(voiceMutex_);
  CancelVoiceOutput(voiceEpoch_, voicePending_, voiceBusy_, voiceAudio_);
  if (voiceBusy_.load()) voiceStatus_.phase = "stopping";
  if (!voiceBusy_.load()) voiceStatus_.phase = voiceStatus_.available ? "idle" : "unavailable";
  ClearVoiceTestContext(voiceTestContext_);
  voiceStatus_.testContextId.clear();
}

void EspIdfBackendNetwork::SetVoicePhase(const char* phase) {
  std::lock_guard<std::mutex> lock(voiceMutex_);
  voiceStatus_.phase = phase;
}

void EspIdfBackendNetwork::RunVoice(DeviceTokenSession& tokens,
                                    ConversationStartCoordinator& coordinator,
                                    ConversationStartRequest& startRequest) {
  ConversationRuntimeSnapshot snapshot;
  std::string requestId, voice, mode;
  uint64_t epoch = 0, queuedAtMs = 0;
  bool imageConsent = false;
  VoiceTestContextLease testContext;
  {
    std::lock_guard<std::mutex> lock(voiceMutex_);
    snapshot = voiceSnapshot_; requestId = voiceRequestId_; voice = voiceStatus_.voice; mode = voiceStatus_.mode;
    epoch = voiceStartEpoch_; queuedAtMs = voiceQueuedAtMs_;
    imageConsent = voiceImageConsent_;
    voiceImageConsent_ = false;
    testContext.value = std::move(voiceTestContext_);
  }
  const bool technicalTest = !testContext.value.empty();
  const auto current = [this, snapshot, epoch, technicalTest, queuedAtMs]() {
    return voiceEpoch_.load() == epoch && !cancelRequested_.load() &&
           (!technicalTest || (MonotonicMs() >= queuedAtMs && MonotonicMs() - queuedAtMs < 180000)) &&
           station_.HasIp() && runtimeState_.Matches(snapshot);
  };
  EspIdfConversationCleanup cleanup(settings_.BackendHost());
  bool succeeded = false;
  TurnOperationObservation final;
  std::string error = "ai_unavailable";
  if (MonotonicMs() - queuedAtMs <= 5000 && current()) {
    VoiceWorkflowRequest request;
    request.snapshot = snapshot; request.requestId = requestId; request.voice = voice;
    request.imageConsent = imageConsent;
    request.correlation.deviceId = identity_.DeviceId(); request.correlation.bootId = bootId_;
    request.correlation.generation = snapshot.generation; request.correlation.deviceRevision = snapshot.deviceRevision;
    request.correlation.operationEpoch = snapshot.operationEpoch;
    VoiceWorkflowPorts ports;
    ports.current = current; ports.uuid = GenerateEspIdfConversationUuid;
    ports.close = [&]() { return CloseConversation(coordinator, cleanup, tokens, startRequest); };
    ports.create = [&](bool photoConsent, const std::string& key, std::string& conversation) {
      startRequest.audioConsent = true; startRequest.imageConsent = photoConsent;
      startRequest.generation = snapshot.generation; startRequest.catalogVersion = snapshot.catalogVersion;
      startRequest.operationEpoch = snapshot.operationEpoch;
      startRequest.idempotencyKey = key;
      startRequest.testContext = testContext.value;
      const auto started = coordinator.Start(startRequest, MonotonicMs());
      ClearVoiceTestContext(startRequest.testContext);
      conversation = coordinator.ConversationId();
      return started == ConversationStartOutcome::Started || started == ConversationStartOutcome::AlreadyStarted;
    };
    ports.photo = [&](const TurnCorrelation& correlation) {
      EspIdfCostumeUpload upload(settings_.BackendHost(), tokens, correlation,
          snapshot.catalogVersion, GenerateEspIdfConversationUuid(), current);
      const auto photo = PrepareCostumePhoto(true, camera_, upload, current, MonotonicMs,
          []() { WaitMs(250); }, [this](const char* phase) { SetVoicePhase(phase); });
      std::lock_guard<std::mutex> lock(voiceMutex_);
      voiceStatus_.cameraAvailable = camera_.Available();
      voiceStatus_.photoWarning = photo == PhotoOutcome::Unavailable ? "photo_unavailable" :
          photo == PhotoOutcome::Failed ? "photo_failed" : "";
      return photo;
    };
    ports.beginAudio = [&]() { voiceAudio_.Continuous(mode == "gpt-live"); };
    ports.realtime = [&](const std::string& body, const std::string& key, const TurnCorrelation& correlation,
        const std::function<bool(const TurnOperationObservation&)>& completed) {
      const char* token = tokens.AccessToken(MonotonicMs());
      // Session creation/photo may have refreshed the same authoritative token.
      showToken_.Publish(token, tokens.AccessTokenExpiresAtMs());
      authExpiresAtMs_.store(tokens.AccessTokenExpiresAtMs());
      authState_.store(token ? 1 : 4);
      EspIdfDeviceMediaTransport transport(settings_.BackendHost(), token ? token : "", voiceAudio_,
          current, [this](const char* phase) { SetVoicePhase(phase); }, mode == "gpt-live",
          [this](const VoiceMediaHealth& health) { PublishVoiceMediaHealth(health); });
      const bool succeeded = transport.Run(body, key, correlation, snapshot, completed);
      if (!transport.Error().empty()) error = transport.Error();
      if (!succeeded && !transport.Diagnostic().empty())
        ESP_LOGW(LogTag, "Device media failed: %.64s", transport.Diagnostic().c_str());
      return succeeded;
    };
    auto result = RunVoiceWorkflow(request, ports);
    succeeded = result.succeeded;
    final = std::move(result.final);
  }
  voiceAudio_.Stop();
  bool proposalStored = false;
  if (succeeded && current() && final.hasProposal) {
    final.proposal.technicalTest = technicalTest;
    const auto utc = std::time(nullptr);
    uint64_t remaining = utc > 0 ? ProposalRemainingMs(final.proposal, static_cast<uint64_t>(utc)) : 0;
    const auto observedAt = MonotonicMs();
    if (technicalTest) remaining = observedAt >= queuedAtMs && observedAt - queuedAtMs < 180000
        ? std::min(remaining, 180000 - (observedAt - queuedAtMs)) : 0;
    KeepsakeOutcome keepsakeCandidate{final.operationId, GenerateEspIdfConversationUuid(), final.proposal.proposalId,
        final.proposal.proposalId, final.proposal.bootId, final.proposal.generation, final.proposal.deviceRevision, 0};
    keepsakeCandidate.compartment = final.proposal.compartment;
    keepsakeCandidate.operationEpoch = final.proposal.operationEpoch;
    const bool scopedCandidate = keepsakeCandidate.operationEpoch != 0;
    const bool keepsakePrepared = remaining && !technicalTest && !scopedCandidate && keepsake_.Prepare(keepsakeCandidate);
    if (remaining && !scopedCandidate && !keepsakePrepared)
      ESP_LOGW(LogTag, "Keepsake preparation unavailable; physical proposal continues");
    {
      std::lock_guard<std::mutex> lock(presentationMutex_);
      proposalStored = remaining && current() && !keepsakeRetirementReason_.front()
          && (!keepsake_.UsesSharedStorage() || scopedCandidate)
          && pendingProposal_.Store(final.proposal, snapshot, runtimeState_, MonotonicMs(), remaining);
      if (proposalStored) {
        terminalProposalOutstanding_ = true;
        keepsakeCandidate_ = scopedCandidate || keepsakePrepared ? keepsakeCandidate : KeepsakeOutcome{};
        keepsakeCandidatePrepared_ = keepsakePrepared;
        technicalCandidate_ = technicalTest;
      }
    }
    if (!proposalStored && keepsakePrepared) keepsake_.Retire(keepsakeCandidate, "proposal_store_rejected");
  }
  if (!proposalStored) CloseConversation(coordinator, cleanup, tokens, startRequest);
  startRequest.audioConsent = false;
  startRequest.imageConsent = false;
  {
    std::lock_guard<std::mutex> lock(voiceMutex_);
    voiceStatus_.phase = succeeded && current() ? "completed" : voiceEpoch_.load() != epoch ? "idle" : "failed";
    voiceStatus_.error = voiceStatus_.phase == "failed" ? error : "";
    voiceBusy_.store(false);
  }
  std::fill(final.reply.begin(), final.reply.end(), '\0');
}

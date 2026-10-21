#pragma once

#include "cJSON.h"
#include <atomic>
#include <mutex>
#include "../voice_turn/LocalVoicePort.h"
#include "../voice_turn/VoiceMediaHealth.h"
#include "../voice_turn/EspIdfVoiceAudio.h"
#include "../keepsake/EspIdfKeepsakeNotifier.h"
#include "../costume_photo/EspIdfCostumeCamera.h"

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

#include "NetworkSettings.h"
#include "../conversation/ConversationRuntimeState.h"
#include "../conversation/PendingProposal.h"
#include "../local_control/LocalConversationNetworkPort.h"
#include "../local_control/LocalConversationReplyPort.h"
#include "../wand_presentation/WandPresentation.h"
#include "DeviceIdentity.h"
#include "../show_cues/DeviceShowCue.h"
#include "../show_cues/BorrowedShowToken.h"

class EspIdfStationNetwork;

class EspIdfBackendNetwork final : public LocalConversationNetworkPort,
                                  public LocalConversationReplyPort,
                                  public LocalVoicePort {
 public:
  EspIdfBackendNetwork(DeviceIdentity& identity,
                       ConversationRuntimeState& runtimeState,
                       const std::string& bootId,
                       EspIdfStationNetwork& station,
                       const CommandJournal& ownerJournal, JournalStorage& journalStorage);
  EspIdfBackendNetwork(DeviceIdentity& identity, ConversationRuntimeState& runtimeState,
                       const std::string& bootId, EspIdfStationNetwork& station,
                       DeviceStorageRuntime& storage);
  bool InitializeVoice(bool nvsInitialized, AudioBoardPort* board = nullptr);
  PresentationAudioPort& PresentationAudio() { return voiceAudio_; }
  VoiceAudioPort& DanceAudio() { return voiceAudio_; }
  void InitializeCamera(bool nvsInitialized, bool profileBVerified);
  LocalVoiceStatus VoiceStatus() const override;
  bool SupportsVoiceTestContext() const override { return keepsake_.UsesSharedStorage(); }
  bool ConfigureVoice(const std::string& mode, const std::string& voice) override;
  bool StartVoice(const std::string& requestId,
                  const ConversationRuntimeSnapshot& snapshot) override;
  bool StartVoiceWithImageConsent(const std::string& requestId,
      const ConversationRuntimeSnapshot& snapshot, bool imageConsent) override;
  bool StartVoiceWithTestContext(const std::string& requestId,
      const ConversationRuntimeSnapshot& snapshot, bool imageConsent,
      const std::string& testContext) override;
  bool StopVoiceForTestContext(const std::string& testContextId) override;
  void StopVoice() override;
  bool Start(bool nvsInitialized);
  bool IsOnline() const { return online_.load(); }
  bool CustomerEnabled() const {
    const auto voice = VoiceStatus();
    return customerEnabled_.load() && voice.available && voice.mode != "off";
  }
  cJSON* CustomerRequest(cJSON* request);
  void PollCustomerStart(const ConversationRuntimeSnapshot& snapshot, bool admitted) override;
  uint32_t AuthenticationState() const;
  uint32_t TrustedTimeState() const { return timeState_.load(); }
  bool ReadVoiceMediaHealth(VoiceMediaHealth& health) const;
  bool IsConversationVoiceBusy() const { return voiceBusy_.load() || voicePending_.load(); }
  bool IsVoiceBusy() const { return IsConversationVoiceBusy() || showCues_.IsActive(); }
  DeviceShowMailbox& ShowCues() { return showCues_; }
  // Explicit retired-text compatibility port. No old HTTP route can be queued.
  bool QueueTextTurn(const std::string&, const std::string&, const std::string&,
                    const ConversationRuntimeSnapshot&) override { return false; }
  void QueueCancel();
  bool QueueCancelFor(const std::string&, uint64_t) override { return false; }
  bool TakeFailedTurnFor(const std::string&, const std::string&, uint64_t) override { return false; }
  bool TakeReply(std::string&, std::string&, std::string&, ConversationRuntimeSnapshot&, uint64_t&) override { return false; }
  bool IsReplyCurrent(uint64_t, const ConversationRuntimeSnapshot&) const override { return false; }
  bool FinishReply(uint64_t) override { return false; }
  bool ReadPendingProposal(TurnProposal& proposal, uint64_t nowMs) const;
  // Control owner calls this after completing the announcement. Network
  // polling never invokes it and cannot directly move an actuator.
  bool BeginPendingPresentation(WandPresentation& presentation,
                                uint64_t nowMs);
  // Called by the control owner after Tick reaches Idle or Fault.
  bool FinishPresentation(const WandPresentation& presentation);

 private:
  void RetireCandidateLocked(const char* reason);
  void ClearCandidateLocked();
  void QueueCancelLocked();
  void StopVoiceOutput();
  static void TaskEntry(void* argument);
  void Run();
  static void ShowTaskEntry(void* argument);
  void RunShowCues();
  void PumpShowCues(const char* token);
  bool CloseConversation(class ConversationStartCoordinator& coordinator,
                         class EspIdfConversationCleanup& transport,
                         class DeviceTokenSession& tokens,
                         struct ConversationStartRequest& startRequest);
  void RunVoice(class DeviceTokenSession& tokens,
                 class ConversationStartCoordinator& coordinator,
                 struct ConversationStartRequest& startRequest);
  void SetVoicePhase(const char* phase);
  bool StartVoiceInternal(const std::string&, const ConversationRuntimeSnapshot&, bool,
                          const std::string&, bool customer);
  static void CustomerTaskEntry(void*);
  void RunCustomer();
  bool RefreshCustomerChallenge(const char* accessToken);
  bool RedeemCustomer(class DeviceTokenSession& tokens, const ConversationRuntimeSnapshot& snapshot,
                      std::string& conversation);
  mutable std::mutex customerMutex_;
  std::atomic<bool> customerEnabled_{false}, customerReserved_{false};
  std::string customerChallenge_, customerOperation_, customerTicket_, customerSession_;
  std::array<uint8_t, 32> customerTicketDigest_{};
  std::string customerState_ = "idle", customerError_, customerReported_;
  ConversationRuntimeSnapshot customerChallengeSnapshot_;
  std::atomic<bool> customerIdleAdmission_{false};
  std::atomic<int> customerPresentationResult_{0};
  bool customerBootReconciled_ = false, customerChallengeRegistering_ = false;
  uint64_t customerChallengeAtMs_ = 0;
  TaskHandle_t customerTask_ = nullptr;
  EspIdfVoiceAudio voiceAudio_;
  EspIdfCostumeCamera camera_;
  bool voiceImageConsent_ = false, voiceCustomer_ = false;
  std::string voiceTestContext_;
  mutable std::mutex voiceMutex_;
  LocalVoiceStatus voiceStatus_;
  ConversationRuntimeSnapshot voiceSnapshot_;
  uint64_t voiceStartEpoch_ = 0, voiceQueuedAtMs_ = 0;
  std::string voiceRequestId_, lastVoiceRequestId_;
  std::atomic<bool> voiceBusy_{false}, voicePending_{false};
  std::atomic<uint64_t> voiceEpoch_{0};

  DeviceIdentity& identity_;
  ConversationRuntimeState& runtimeState_;
  EspIdfStationNetwork& station_;
  const NetworkSettings& settings_;
  PendingProposal pendingProposal_;
  std::mutex presentationMutex_;
  bool terminalProposalOutstanding_ = false;
  bool presentationActive_ = false;
  // Protected by presentationMutex_. The original proposal is now owned by
  // Wand, so network expiry must not reread the consumed PendingProposal.
  bool presentationClaimed_ = false;
  std::atomic<bool> online_{false};
  std::atomic<uint32_t> authState_{0}, timeState_{0};
  std::atomic<uint64_t> authExpiresAtMs_{0};
  mutable std::mutex mediaHealthMutex_;
  VoiceMediaHealth mediaHealth_;
  void PublishVoiceMediaHealth(const VoiceMediaHealth& health);
  EspIdfKeepsakeNotifier keepsake_;
  KeepsakeOutcome keepsakeCandidate_;
  bool keepsakeCandidatePrepared_ = false;
  bool technicalCandidate_ = false;
  EspIdfKeepsakeNotifier::Handle keepsakeHandle_;
  std::array<char, 65> keepsakeRetirementReason_{};
  std::atomic<bool> cancelRequested_{false};
  std::string bootId_;
  bool closePending_ = false;
  uint64_t closeRetryAtMs_ = 0;
  uint32_t closeRetryDelayMs_ = 1000;
  std::string deleteIdempotencyKey_;
  TaskHandle_t task_ = nullptr;
  DeviceShowMailbox showCues_;
  BorrowedShowToken showToken_;
  std::atomic<bool> showUnauthorized_{false};
  TaskHandle_t showTask_ = nullptr;
};

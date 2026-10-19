#pragma once

#include <atomic>
#include <mutex>
#include "../voice_turn/LocalVoicePort.h"
#include "../voice_turn/EspIdfVoiceAudio.h"
#include "../keepsake/EspIdfKeepsakeNotifier.h"
#include "../costume_photo/EspIdfCostumeCamera.h"

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

#include "NetworkSettings.h"
#include "../conversation/ConversationRuntimeState.h"
#include "../conversation/ConversationTextInbox.h"
#include "../conversation/ConversationReplyInbox.h"
#include "../conversation/ConversationTurnFailureInbox.h"
#include "../conversation/PendingProposal.h"
#include "../local_control/LocalConversationNetworkPort.h"
#include "../local_control/LocalConversationReplyPort.h"
#include "../wand_presentation/WandPresentation.h"
#include "../provisioning/DeviceIdentity.h"
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
  bool InitializeVoice(bool nvsInitialized, AudioBoardPort* board = nullptr);
  PresentationAudioPort& PresentationAudio() { return voiceAudio_; }
  VoiceAudioPort& DanceAudio() { return voiceAudio_; }
  void InitializeCamera(bool nvsInitialized, bool profileBVerified);
  LocalVoiceStatus VoiceStatus() const override;
  bool ConfigureVoice(const std::string& mode, const std::string& voice) override;
  bool StartVoice(const std::string& requestId,
                  const ConversationRuntimeSnapshot& snapshot) override;
  bool StartVoiceWithImageConsent(const std::string& requestId,
      const ConversationRuntimeSnapshot& snapshot, bool imageConsent) override;
  void StopVoice() override;
  bool Start(bool nvsInitialized);
  bool IsOnline() const { return online_.load(); }
  bool IsConversationVoiceBusy() const { return voiceBusy_.load() || voicePending_.load(); }
  bool IsVoiceBusy() const { return IsConversationVoiceBusy() || showCues_.IsActive(); }
  DeviceShowMailbox& ShowCues() { return showCues_; }
  bool QueueTextTurn(const std::string& text,
                     const std::string& conversationId,
                     const std::string& clientTurnId,
                     const ConversationRuntimeSnapshot& expectedSnapshot) override;
  // Called only for a deliberate local start event, never by reconnect.
  bool QueueOpening(const std::string& conversationId,
                    const ConversationRuntimeSnapshot& expectedSnapshot);
  void QueueCancel();
  bool QueueCancelFor(
      const std::string& conversationId,
      uint64_t snapshotVersion) override;
  bool TakeFailedTurnFor(
      const std::string& conversationId,
      const std::string& clientTurnId,
      uint64_t snapshotVersion) override;
  // The caller must check IsReplyCurrent before and during playback, then
  // FinishReply. Stop/cancel must also stop the actual output port.
  bool TakeReply(std::string& reply, std::string& conversationId,
                 std::string& clientTurnId,
                 ConversationRuntimeSnapshot& snapshot, uint64_t& token) override;
  bool IsReplyCurrent(
      uint64_t token, const ConversationRuntimeSnapshot& snapshot) const override;
  bool FinishReply(uint64_t token) override;
  bool ReadPendingProposal(TurnProposal& proposal, uint64_t nowMs) const;
  // Control owner calls this after completing the announcement. Network
  // polling never invokes it and cannot directly move an actuator.
  bool BeginPendingPresentation(WandPresentation& presentation,
                                uint64_t nowMs);
  // Called by the control owner after Tick reaches Idle or Fault.
  bool FinishPresentation(const WandPresentation& presentation);

 private:
  void RetireCandidateLocked(const char* reason);
  void QueueCancelLocked();
  void StopVoiceOutput();
  static void TaskEntry(void* argument);
  void Run();
  static void ShowTaskEntry(void* argument);
  void RunShowCues();
  void PumpShowCues(const char* token);
  void RunTurn(const std::string& text,
               const std::string& conversationId,
               const std::string& clientTurnId, bool opening,
               uint64_t snapshotVersion,
               class DeviceTokenSession& tokens,
               EspIdfStationNetwork& station,
               class ConversationStartCoordinator& coordinator,
               struct ConversationStartRequest& startRequest);
  bool CloseConversation(class ConversationStartCoordinator& coordinator,
                         class EspIdfConversationTextTransport& transport,
                         class DeviceTokenSession& tokens,
                         struct ConversationStartRequest& startRequest);
  bool IsLocalConversationAdmitted(
      const std::string& normalizedConversationId,
      uint64_t snapshotVersion);
  bool StoreReplyIfAdmitted(
      const std::string& reply, const std::string& conversationId,
      const std::string& clientTurnId,
      const ConversationRuntimeSnapshot& snapshot);
  void FailQueuedText();
  void DiscardTextUnless(const std::string& conversationId);

  void RunVoice(class DeviceTokenSession& tokens,
                 class ConversationStartCoordinator& coordinator,
                 struct ConversationStartRequest& startRequest);
  void SetVoicePhase(const char* phase);
  EspIdfVoiceAudio voiceAudio_;
  EspIdfCostumeCamera camera_;
  bool voiceImageConsent_ = false;
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
  ConversationTextInbox textInbox_;
  ConversationReplyInbox replyInbox_;
  ConversationTurnFailureInbox failedTurns_;
  PendingProposal pendingProposal_;
  std::mutex presentationMutex_;
  bool terminalProposalOutstanding_ = false;
  bool presentationActive_ = false;
  std::mutex openingMutex_;
  bool openingPending_ = false;
  std::string openingConversationId_;
  uint64_t openingSnapshotVersion_ = 0;
  // Admission survives a failed backend start until the device snapshot changes.
  std::string admittedLocalConversationId_;
  uint64_t admittedSnapshotVersion_ = 0;
  std::atomic<bool> online_{false};
  EspIdfKeepsakeNotifier keepsake_;
  KeepsakeOutcome keepsakeCandidate_;
  std::atomic<bool> cancelRequested_{false};
  std::string bootId_;
  // Only the network task mutates this binding. Local and backend IDs differ.
  std::string activeLocalConversationId_;
  uint64_t startSnapshotVersion_ = 0;
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

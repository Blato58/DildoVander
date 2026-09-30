#pragma once

#include <atomic>
#include <mutex>

#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

#include "NetworkSettings.h"
#include "../conversation/ConversationRuntimeState.h"
#include "../conversation/ConversationTextInbox.h"
#include "../conversation/ConversationReplyInbox.h"
#include "../conversation/PendingProposal.h"
#include "../wand_presentation/WandPresentation.h"
#include "../provisioning/DeviceIdentity.h"

class EspIdfBackendNetwork final {
 public:
  EspIdfBackendNetwork(DeviceIdentity& identity,
                       ConversationRuntimeState& runtimeState);
  bool Start(bool nvsInitialized);
  bool QueueTextTurn(const std::string& text,
                     const std::string& conversationId,
                     const std::string& clientTurnId,
                     const ConversationRuntimeSnapshot& expectedSnapshot);
  // Called only for a deliberate local start event, never by reconnect.
  bool QueueOpening(const std::string& conversationId,
                    const ConversationRuntimeSnapshot& expectedSnapshot);
  void QueueCancel();
  // The caller must check IsReplyCurrent before and during playback, then
  // FinishReply. Stop/cancel must also stop the actual output port.
  bool TakeReply(std::string& reply, std::string& conversationId,
                 std::string& clientTurnId,
                 ConversationRuntimeSnapshot& snapshot, uint64_t& token);
  bool IsReplyCurrent(
      uint64_t token, const ConversationRuntimeSnapshot& snapshot) const;
  bool FinishReply(uint64_t token);
  bool ReadPendingProposal(TurnProposal& proposal, uint64_t nowMs) const;
  // Control owner calls this after completing the announcement. Network
  // polling never invokes it and cannot directly move an actuator.
  bool BeginPendingPresentation(WandPresentation& presentation,
                                uint64_t nowMs);
  // Called by the control owner after Tick reaches Idle or Fault.
  bool FinishPresentation(const WandPresentation& presentation);

 private:
  static void TaskEntry(void* argument);
  void Run();
  void RunTurn(const std::string& text,
               const std::string& conversationId,
               const std::string& clientTurnId, bool opening,
               uint64_t snapshotVersion,
                   class DeviceTokenSession& tokens,
                   class EspIdfWifiStation& wifi,
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

  DeviceIdentity& identity_;
  ConversationRuntimeState& runtimeState_;
  NetworkSettings settings_;
  ConversationTextInbox textInbox_;
  ConversationReplyInbox replyInbox_;
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
};

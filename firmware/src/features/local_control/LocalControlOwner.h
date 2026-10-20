#pragma once

#include <memory>

#include "LocalControlInbox.h"
#include "LocalControlOutputPort.h"
#include "LocalControlSession.h"
#include "LocalConversationGateway.h"
#include "../dance/DancePlayerPort.h"
#include "../device_control/DeviceControlRuntime.h"

class LocalVoicePort;
class LocalControlTransport;
class LocalConversationNetworkPort;
class ConversationRuntimeState;
class LocalConversationReplyPort;
namespace kufr { namespace offline { class OfflineShow; } }

// One control-task consumer for a transport already admitted by TLS/bearer.
// Tick owns Poll + runtime Tick under an urgent-event guard. Do not tick the
// runtime separately while this owner is attached. Physical/step ports must be
// bounded and must not reenter the inbox or transport from guarded actions.
class LocalControlOwner final {
 public:
  LocalControlOwner(const std::string& deviceId, const std::string& bootId,
                    DeviceControlRuntime& runtime,
                    ConversationRuntimeState& runtimeState,
                    ConversationSession& conversation,
                    LocalConversationNetworkPort& network,
                    LocalControlInbox& inbox, LocalControlTransport& transport,
                    LocalControlOutputPort& output, LocalControlIdPort& ids,
                    LocalControlRecoveryGate& recovery,
                    LocalControlStepPort& steps, LocalControlClockPort& clock,
                    LocalVoicePort* voice = nullptr,
                    kufr::dance::LocalDancePort* dance = nullptr);
  ~LocalControlOwner();

  void Poll();
  void Tick();
  void SetMaintenance(bool active) { maintenance_ = active; }
  void SetRecoveryAdmission(bool allowed) { recoveryAdmission_ = allowed; }
  void SetDanceAdmission(bool allowed) { danceAdmission_ = allowed; }
  void BindOffline(kufr::offline::OfflineShow& offline) { offline_ = &offline; }
  void PumpReply(LocalConversationReplyPort& replies);
  bool CompleteTurn(const std::string& clientTurnId, uint64_t generation);

 private:
  void Connected(const LocalControlEvent& event);
  void Disconnect();
  void Close();
  void Handle(const LocalControlMessage& message, uint64_t nowMs);
  void Selection(const LocalControlMessage& message, uint64_t nowMs);
  void Reload(const LocalControlMessage& message, uint64_t nowMs);
  void ManualCommand(const LocalControlMessage& message, uint64_t nowMs);
  CommandReceipt AcceptManualCommand(const LocalControlMessage& message);
  void FinishManualCommand(const LocalControlMessage& message,
                           const CommandReceipt& receipt, uint64_t nowMs);
  void ClearPendingManual();
  void OfflineCommand(const LocalControlMessage& message, uint64_t nowMs);
  bool AcceptOfflineCommand(const LocalControlMessage& message, bool retry);
  void FinishOfflineCommand(const LocalControlMessage& message, bool result, uint64_t nowMs);
  void ClearPendingOffline();
  void Reset(const LocalControlMessage& message, uint64_t nowMs);
  void FinishReset(const LocalControlMessage& message, DeviceControlRuntime::RecoveryProgress progress, uint64_t nowMs);
  void ClearPendingRecovery();
  bool ControlAck(const LocalControlMessage& message, LocalControlError error);
  bool RefuseBusy(const LocalControlMessage& message);
  void Snapshot(uint64_t nowMs);
  bool SameHandle(const LocalSocketAdmission::Handle& handle) const;

  LocalVoicePort* voice_;
  kufr::dance::LocalDancePort* dance_;
  std::string deviceId_;
  std::string bootId_;
  DeviceControlRuntime& runtime_;
  ConversationRuntimeState& runtimeState_;
  ConversationSession& conversation_;
  LocalConversationNetworkPort& network_;
  LocalControlInbox& inbox_;
  LocalControlTransport& transport_;
  LocalControlOutputPort& output_;
  LocalControlIdPort& ids_;
  LocalControlRecoveryGate& recovery_;
  LocalControlStepPort& steps_;
  LocalControlClockPort& clock_;
  LocalSocketAdmission::Handle handle_;
  std::unique_ptr<LocalControlSession> session_;
  std::unique_ptr<LocalConversationGateway> gateway_;
  uint64_t connectedAtMs_ = 0;
  uint64_t lastSnapshotAtMs_ = 0;
  LocalControlMessage pendingManual_;
  LocalSocketAdmission::Handle pendingManualHandle_;
  LocalControlSession* pendingManualSession_ = nullptr;
  bool manualPending_ = false;
  LocalControlMessage pendingOffline_;
  LocalSocketAdmission::Handle pendingOfflineHandle_;
  LocalControlSession* pendingOfflineSession_ = nullptr;
  uint64_t offlineArmGeneration_ = 0, offlineArmRevision_ = 0;
  bool offlinePending_ = false, offlineArmHandoff_ = false, offlineCallPending_ = false;
  DeviceControlRuntime::RecoveryHandle recoveryHandle_;
  LocalControlMessage pendingRecovery_;
  LocalSocketAdmission::Handle pendingRecoveryHandle_;
  LocalControlSession* pendingRecoverySession_ = nullptr;
  bool recoveryPending_ = false, recoveryAdmission_ = false;
  bool hello_ = false;
  bool includeOperationEpoch_ = false;
  bool maintenance_ = false;
  bool danceAdmission_ = false;
  kufr::offline::OfflineShow* offline_ = nullptr;
};

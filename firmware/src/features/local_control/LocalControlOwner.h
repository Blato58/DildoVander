#pragma once

#include <memory>

#include "LocalControlInbox.h"
#include "LocalControlOutputPort.h"
#include "LocalControlSession.h"
#include "LocalConversationGateway.h"
#include "../dance/DancePlayerPort.h"

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
  bool hello_ = false;
  bool maintenance_ = false;
  bool danceAdmission_ = false;
  kufr::offline::OfflineShow* offline_ = nullptr;
};

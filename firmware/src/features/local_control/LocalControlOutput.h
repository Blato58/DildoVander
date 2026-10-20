#pragma once

#include "LocalControlOutputPort.h"
#include "../dance/DancePlayerPort.h"

class LocalVoicePort;
class DeviceControl;
class PersistentCommandJournal;
class SafetyController;
class SelectionSequence;
class LocalControlTransport;

// Reads actual state only on the control owner; never a parallel device model.
class LocalControlOutput final : public LocalControlOutputPort {
 public:
  LocalControlOutput(const std::string& deviceId, const std::string& bootId,
                     DeviceControl& control, PersistentCommandJournal& journal,
                     SafetyController& safety, SelectionSequence& selection,
                     ConversationSession& conversation,
                     LocalControlTransport& transport, LocalVoicePort* voice = nullptr,
                     kufr::dance::LocalDancePort* dance = nullptr);

  bool VoiceAck(const LocalSocketAdmission::Handle& handle,
                const LocalControlMessage& request, bool accepted) override;
  bool OfflineAck(const LocalSocketAdmission::Handle& handle,
      const LocalControlMessage& request, bool accepted, const std::string& error,
      const std::string& status, const std::string& records = "") override;
  bool DanceAck(const LocalSocketAdmission::Handle& handle,
                const LocalControlMessage& request, bool accepted) override;
  bool ControlAck(const LocalSocketAdmission::Handle& handle,
                  const LocalControlMessage& request, LocalControlError error) override;
  bool Welcome(const LocalSocketAdmission::Handle& handle,
                const std::string& requestId, uint64_t nowMs) override;
  bool Snapshot(const LocalSocketAdmission::Handle& handle,
                 const std::string& leaseId, uint64_t nowMs) override;
  bool Heartbeat(const LocalSocketAdmission::Handle& handle,
                  const LocalControlMessage& request, uint64_t nowMs) override;
  bool CommandAck(const LocalSocketAdmission::Handle& handle,
                   const LocalControlMessage& request,
                   const CommandReceipt& receipt) override;
  bool ConversationAck(const LocalSocketAdmission::Handle& handle,
                        const LocalControlMessage& request,
                        const ConversationCommandReceipt& receipt) override;
  bool ConversationResult(const LocalSocketAdmission::Handle& handle,
                          const std::string& clientTurnId, uint64_t generation,
                          const std::string& reply) override;

 private:
  bool AdvanceEvent();
  bool BuildSnapshot(const std::string& leaseId, uint64_t nowMs,
                      std::string& json);
  bool BuildResult(uint64_t eventSequence, std::string& json) const;
  bool Send(const LocalSocketAdmission::Handle& handle, const std::string& json);
  bool ResultChanged() const;
  void RememberResult();
  const char* Phase(bool liveLease) const;

  LocalVoicePort* voice_;
  kufr::dance::LocalDancePort* dance_;
  std::string deviceId_;
  std::string bootId_;
  DeviceControl& control_;
  PersistentCommandJournal& journal_;
  SafetyController& safety_;
  SelectionSequence& selection_;
  ConversationSession& conversation_;
  LocalControlTransport& transport_;
  uint64_t eventSequence_ = 0;
  uint64_t resultEventSequence_ = 0;
  LastSelectionResult rememberedResult_;
};

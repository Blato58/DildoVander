#pragma once

#include "LocalControlCodec.h"
#include "LocalSocketAdmission.h"
#include "../conversation/ConversationSession.h"

class LocalControlOutputPort {
 public:
  virtual ~LocalControlOutputPort() = default;
  virtual bool OfflineAck(const LocalSocketAdmission::Handle&,
      const LocalControlMessage&, bool, const std::string&, const std::string&,
      const std::string& = "") { return false; }
  virtual bool VoiceAck(const LocalSocketAdmission::Handle&,
                        const LocalControlMessage&, bool) { return false; }
  virtual bool DanceAck(const LocalSocketAdmission::Handle&,
                        const LocalControlMessage&, bool) { return false; }
  virtual bool Welcome(const LocalSocketAdmission::Handle& handle,
                       const std::string& requestId, uint64_t nowMs) = 0;
  virtual bool Snapshot(const LocalSocketAdmission::Handle& handle,
                        const std::string& leaseId, uint64_t nowMs) = 0;
  virtual bool Heartbeat(const LocalSocketAdmission::Handle& handle,
                         const LocalControlMessage& request,
                         uint64_t nowMs) = 0;
  virtual bool CommandAck(const LocalSocketAdmission::Handle& handle,
                          const LocalControlMessage& request,
                          const CommandReceipt& receipt) = 0;
  virtual bool ConversationAck(const LocalSocketAdmission::Handle& handle,
                               const LocalControlMessage& request,
                               const ConversationCommandReceipt& receipt) = 0;
  virtual bool ConversationResult(const LocalSocketAdmission::Handle& handle,
                                  const std::string& clientTurnId,
                                  uint64_t generation,
                                  const std::string& reply) = 0;
};

class LocalControlIdPort {
 public:
  virtual ~LocalControlIdPort() = default;
  virtual std::string NewLeaseId() = 0;
};

class LocalControlRecoveryGate {
 public:
  virtual ~LocalControlRecoveryGate() = default;
  virtual bool InspectionAllowsRecovery() const = 0;
};

class LocalControlStepPort {
 public:
  virtual ~LocalControlStepPort() = default;
  virtual void OnStep(const SelectionStep& step) = 0;
};

class LocalControlClockPort {
 public:
  virtual ~LocalControlClockPort() = default;
  virtual uint64_t NowMs() const = 0;
};

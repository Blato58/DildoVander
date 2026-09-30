#pragma once

#include <stdint.h>

#include <string>

class DeviceControlRuntime;
struct SelectionStep;

// Created only after the transport has authenticated one control socket.
// All methods run on the control owner task, never on the WSS network task.
class LocalControlSession final {
 public:
  LocalControlSession(DeviceControlRuntime& runtime,
                      const std::string& deviceId,
                      const std::string& bootId,
                      uint64_t connectedAtMs);

  bool Hello(uint32_t protocolVersion, const std::string& deviceId,
             uint64_t nowMs);
  bool Acquire(const std::string& bootId, const std::string& leaseId,
               uint64_t nowMs);
  bool Heartbeat(const std::string& bootId, const std::string& leaseId,
                 uint64_t nowMs);
  bool OwnsLiveLease(const std::string& bootId,
                     const std::string& leaseId, uint64_t nowMs) const;
  bool Release(const std::string& bootId, const std::string& leaseId,
               SelectionStep& step);
  void Disconnect(SelectionStep& step);
  const std::string& LeaseId() const;

 private:
  bool MatchesOwnedLease(const std::string& bootId,
                         const std::string& leaseId) const;

  DeviceControlRuntime& runtime_;
  std::string deviceId_;
  std::string bootId_;
  std::string leaseId_;
  uint64_t connectedAtMs_ = 0;
  bool helloComplete_ = false;
  bool disconnected_ = false;
};

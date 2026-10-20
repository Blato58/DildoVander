#pragma once
#include "DeviceShowCue.h"

class EspIdfDeviceShowTransport final {
 public:
  explicit EspIdfDeviceShowTransport(const std::string& host);
  int Poll(const std::string& bootId, uint64_t generation, uint64_t revision, uint64_t deviceMs,
      const DeviceShowSettings& applied, const std::string& voice, const char* token, DeviceShowEnvelope& envelope);
  int Ack(const DeviceShowAck& ack, const char* token);
  int Download(const DeviceShowCue& cue, const char* token,
      DeviceShowAudio& audio, const DeviceShowMailbox& mailbox);
  static std::shared_ptr<DeviceShowAudio> AllocateAudio();
 private:
  int JsonRequest(const std::string& path, const std::string& body, const char* token, std::string& response);
  std::string root_;
};

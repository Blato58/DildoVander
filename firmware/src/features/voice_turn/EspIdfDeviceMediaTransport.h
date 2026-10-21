#pragma once
#include <functional>
#include <string>
#include "EspIdfVoiceAudio.h"
#include "DeviceMediaFailure.h"
#include "VoiceMediaHealth.h"
#include "../conversation/ConversationRuntimeState.h"
#include "../conversation/TurnOperationCodec.h"

// Runs on the backend network worker. Raw playback has its own worker; neither
// socket waits nor teardown join run on the actuator/control owner.
class EspIdfDeviceMediaTransport final {
 public:
  EspIdfDeviceMediaTransport(const std::string& host, const std::string& token,
      EspIdfVoiceAudio& audio, std::function<bool()> current,
      std::function<void(const char*)> phase, bool live,
      std::function<void(const VoiceMediaHealth&)> health = {});
  ~EspIdfDeviceMediaTransport();
  bool Run(const std::string& body, const std::string& key, const TurnCorrelation& opening,
      const ConversationRuntimeSnapshot& snapshot,
      const std::function<bool(const TurnOperationObservation&)>& completed);
  std::string Error() const { return error_.empty() ? "" : PublicDeviceMediaError(error_.c_str()); }
  const std::string& Diagnostic() const { return error_; }
 private:
  std::string host_, token_, error_;
  EspIdfVoiceAudio& audio_;
  std::function<bool()> current_;
  std::function<void(const char*)> phase_;
  bool live_;
  std::function<void(const VoiceMediaHealth&)> health_;
};

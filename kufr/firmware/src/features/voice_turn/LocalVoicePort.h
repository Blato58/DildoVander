#pragma once
#include <string>
#include "../conversation/ConversationRuntimeState.h"
#include "../keepsake/KeepsakeOutbox.h"

struct LocalVoiceStatus {
  bool available = false;
  std::string mode = "off";
  std::string voice = "marin";
  std::string phase = "unavailable";
  std::string error = "";
  bool cameraAvailable = false;
  std::string photoWarning;
};

class LocalVoicePort {
 public:
  virtual ~LocalVoicePort() = default;
  virtual LocalVoiceStatus VoiceStatus() const = 0;
  virtual bool ConfigureVoice(const std::string& mode, const std::string& voice) = 0;
  virtual bool StartVoice(const std::string& requestId,
                          const ConversationRuntimeSnapshot& snapshot) = 0;
  virtual void StopVoice() = 0;
  virtual LocalKeepsakeStatus KeepsakeStatus(uint64_t) const { return {}; }
  virtual bool StartVoiceWithImageConsent(const std::string& requestId,
      const ConversationRuntimeSnapshot& snapshot, bool imageConsent) {
    return !imageConsent && StartVoice(requestId, snapshot);
  }
};

inline std::string VoiceStatusJson(const LocalVoiceStatus& status) {
  // Fields originate exclusively from fixed firmware enums, never provider text.
  return "{\"available\":" + std::string(status.available ? "true" : "false") +
      ",\"mode\":\"" + status.mode + "\",\"voice\":\"" + status.voice +
      "\",\"phase\":\"" + status.phase + "\",\"error\":" +
      (status.error.empty() ? "null" : "\"" + status.error + "\"") +
      ",\"cameraAvailable\":" + (status.cameraAvailable ? "true" : "false") +
      ",\"photoWarning\":" + (status.photoWarning.empty() ? "null" : "\"" + status.photoWarning + "\"") + "}";
}

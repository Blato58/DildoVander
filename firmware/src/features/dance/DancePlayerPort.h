#pragma once
#include <cstdint>
#include <string>

namespace kufr { namespace dance {
struct DanceStatus {
  bool available = false;
  std::string phase = "unavailable";
  std::string track;
  std::string error;
  uint32_t positionMs = 0;
  uint32_t durationMs = 0;
  uint16_t bpm = 0;
  uint32_t beatOffsetMs = 0;
  uint8_t red = 0, green = 0, blue = 0;
  uint8_t vibrationEveryBeats = 0;
};

inline bool IsDanceActive(const DanceStatus& status) {
  return status.phase == "starting" || status.phase == "playing" || status.phase == "stopping";
}

// Start/Stop and effect transitions are owned by the control task. The player
// performs bounded SD/audio I/O on its worker; status is a coherent snapshot.
class DancePlayerPort {
 public:
  virtual ~DancePlayerPort() = default;
  virtual DanceStatus Status() const = 0;
  virtual bool Start(const std::string& requestId) = 0;
  virtual void Stop() = 0;
};

class LocalDancePort {
 public:
  virtual ~LocalDancePort() = default;
  virtual DanceStatus DanceState() const = 0;
  virtual bool StartDance(const std::string& requestId) = 0;
  virtual void StopDance() = 0;
};
} }

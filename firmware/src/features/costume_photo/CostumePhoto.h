#pragma once
#include <functional>
#include <string>
#include <vector>
#include "../conversation/TurnPollGuard.h"

constexpr size_t MaximumCostumeJpegBytes = 262144;
void WipeCostumeJpeg(std::vector<uint8_t>& jpeg);
bool ValidCostumeJpeg(const uint8_t* data, size_t size);

enum class PhotoPoll { Pending, Completed, Failed };
enum class PhotoOutcome { Skipped, Unavailable, Completed, Failed, Cancelled };

class CostumeCameraPort {
 public:
  virtual ~CostumeCameraPort() = default;
  virtual bool Available() const = 0;
  virtual bool Capture(std::vector<uint8_t>& jpeg) = 0;
  virtual bool Capture(std::vector<uint8_t>& jpeg, const std::function<bool()>& current) {
    return current() && Capture(jpeg);
  }
};
class CostumeUploadPort {
 public:
  virtual ~CostumeUploadPort() = default;
  virtual bool Upload(const std::vector<uint8_t>& jpeg, std::string& operationId) = 0;
  virtual PhotoPoll Poll(const std::string& operationId) = 0;
};

// One invocation belongs to one admitted conversation. Never retry an upload;
// an uncertain network response must not cause another paid image analysis.
PhotoOutcome PrepareCostumePhoto(bool consent, CostumeCameraPort& camera,
    CostumeUploadPort& transport, const std::function<bool()>& current,
    const std::function<uint64_t()>& now, const std::function<void()>& wait,
    const std::function<void(const char*)>& phase = [](const char*) {});
bool ParseImageAccepted(const std::string& json, std::string& operationId);
PhotoPoll ParseImageOperation(const std::string& json,
    const std::string& operationId, const TurnCorrelation& expected);

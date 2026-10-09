#pragma once
#include "AssetReader.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"

namespace kufr { namespace assets {
struct SdStatus { std::string state; std::string error; };
// App-lifetime optional worker. Tick is called only by the common control owner;
// every activity admission must respect Reserved while SD I/O is running.
class EspIdfSdAssets final {
 public:
  bool Begin();
  bool RequestMount();
  bool RequestRead(const std::string& name, size_t maximumBytes);
  bool RequestStat(const std::string& name);
  bool RequestUnmount();
  void Tick(bool safeIdle);
  bool TakeReadResult(AssetBytes& output, std::string& error);
  bool TakeStatResult(AssetInfo& info, std::string& error);
  bool Reserved() const;
  SdStatus Status() const;
 private:
  friend struct SdAssetsTestAccess;
  enum class Work { None, Mount, Read, Stat, Unmount };
  static void WorkerEntry(void* owner);
  void Worker();
  void Execute(Work work);
  bool LoadVerified() const;
  mutable SemaphoreHandle_t mutex_ = nullptr;
  TaskHandle_t task_ = nullptr;
  void* card_ = nullptr;
  SdStatus status_;
  Work pending_ = Work::None;
  Work active_ = Work::None;
  bool verified_ = false;
  bool reserved_ = false;
  bool resultReady_ = false;
  bool statReady_ = false;
  std::string name_;
  size_t maximumBytes_ = 0;
  AssetBytes result_;
  std::string resultError_;
  AssetInfo resultInfo_;
};
} }

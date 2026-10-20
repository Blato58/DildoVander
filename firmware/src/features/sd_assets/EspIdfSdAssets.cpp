#include "EspIdfSdAssets.h"
#include <cstdio>
#include <sys/stat.h>
#include "driver/sdmmc_host.h"
#include "esp_timer.h"
#include "esp_vfs_fat.h"
#include "nvs.h"
#include "sdmmc_cmd.h"

namespace kufr { namespace assets {
namespace {
constexpr const char* MountPath = "/sd-assets";
class Lock final {
 public:
  explicit Lock(SemaphoreHandle_t mutex) : mutex_(mutex) { xSemaphoreTake(mutex_, portMAX_DELAY); }
  ~Lock() { xSemaphoreGive(mutex_); }
 private:
  SemaphoreHandle_t mutex_;
};
esp_err_t ReadOnlyTransaction(int slot, sdmmc_command_t* command) {
  if (!command || (command->datalen && !(command->flags & SCF_CMD_READ)) ||
      command->opcode == 20 || command->opcode == 24 || command->opcode == 25 ||
      command->opcode == 32 || command->opcode == 33 || command->opcode == 35 ||
      command->opcode == 36 || command->opcode == 38) return ESP_ERR_NOT_SUPPORTED;
  return sdmmc_host_do_transaction(slot, command);
}
class FatFile final : public AssetFilePort {
 public:
  ~FatFile() override { Close(); }
  bool Open(const std::string& name, AssetInfo& info) override {
    Close();
    if (!IsAssetFilename(name)) return false;
    const std::string path = std::string(MountPath) + "/" + name;
    // FAT has no symlink representation. The fixed mount is only this card,
    // and flat names cannot traverse into another VFS mount.
    file_ = std::fopen(path.c_str(), "rb");
    if (!file_) return false;
    struct stat attributes{};
    if (fstat(fileno(file_), &attributes) != 0 || attributes.st_size < 0) return false;
    info.size = static_cast<uint64_t>(attributes.st_size);
    info.regular = S_ISREG(attributes.st_mode);
    info.symlink = false;
    original_ = attributes;
    return true;
  }
  int Read(uint8_t* bytes, size_t count) override {
    const size_t received = std::fread(bytes, 1, count, file_);
    vTaskDelay(1);
    return std::ferror(file_) ? -1 : static_cast<int>(received);
  }
  bool Unchanged(const AssetInfo& original) override {
    struct stat current{};
    return fstat(fileno(file_), &current) == 0 && S_ISREG(current.st_mode) &&
        current.st_size >= 0 && static_cast<uint64_t>(current.st_size) == original.size &&
        current.st_mtime == original_.st_mtime;
  }
  void Close() override { if (file_) { std::fclose(file_); file_ = nullptr; } }
  uint64_t NowMs() const override { return static_cast<uint64_t>(esp_timer_get_time() / 1000); }
 private:
  FILE* file_ = nullptr;
  struct stat original_{};
};
}
bool EspIdfSdAssets::LoadVerified() const {
  nvs_handle_t nvs;
  if (nvs_open("wand_hw", NVS_READONLY, &nvs) != ESP_OK) return false;
  uint8_t profile = 0, sd = 0;
  const bool loaded = nvs_get_u8(nvs, "profile_b_ok", &profile) == ESP_OK &&
      nvs_get_u8(nvs, "sd_verified", &sd) == ESP_OK;
  nvs_close(nvs);
  return loaded && profile == 1 && sd == 1;
}
bool EspIdfSdAssets::Begin() {
  if (mutex_) return false;
  mutex_ = xSemaphoreCreateMutex();
  if (!mutex_) return false;
  verified_ = LoadVerified();
  status_.state = verified_ ? "unmounted" : "disabled";
  if (!verified_) { status_.error = "sd-wiring-not-verified"; return true; }
  if (xTaskCreate(WorkerEntry, "kufr-sd-assets", 6144, this, 2, &task_) != pdPASS) {
    verified_ = false; status_ = {"disabled", "sd-worker-unavailable"}; return false;
  }
  pending_ = Work::Mount;  // One optional attempt, started only by safe owner Tick.
  status_.state = "waiting-idle";
  return true;
}
SdStatus EspIdfSdAssets::Status() const {
  if (!mutex_) return {"disabled", "sd-not-started"};
  Lock lock(mutex_); return status_;
}
bool EspIdfSdAssets::Reserved() const {
  if (!mutex_) return false;
  Lock lock(mutex_); return reserved_ || playbackReserved_;
}
bool EspIdfSdAssets::TryReservePlayback() {
  if (!mutex_) return false;
  Lock lock(mutex_);
  if (!verified_ || !card_ || playbackReserved_ || reserved_ ||
      active_ != Work::None || pending_ != Work::None || resultReady_ || statReady_) return false;
  playbackReserved_ = true;
  return true;
}
void EspIdfSdAssets::ReleasePlayback() {
  if (!mutex_) return;
  Lock lock(mutex_); playbackReserved_ = false;
}
bool EspIdfSdAssets::PlaybackReserved() const {
  if (!mutex_) return false;
  Lock lock(mutex_); return playbackReserved_;
}
bool EspIdfSdAssets::RequestMount() {
  if (!mutex_) return false;
  Lock lock(mutex_);
  if (!verified_ || card_ || playbackReserved_ || active_ != Work::None || pending_ != Work::None || resultReady_ || statReady_) return false;
  pending_ = Work::Mount; status_.state = "waiting-idle"; return true;
}
bool EspIdfSdAssets::RequestRead(const std::string& name, size_t maximumBytes) {
  if (!mutex_ || !IsAssetFilename(name) || maximumBytes == 0 || maximumBytes > MaximumAssetBytes) return false;
  Lock lock(mutex_);
  if (!card_ || playbackReserved_ || active_ != Work::None || pending_ != Work::None || resultReady_ || statReady_) return false;
  name_ = name; maximumBytes_ = maximumBytes;
  pending_ = Work::Read; status_.state = "waiting-idle"; return true;
}
bool EspIdfSdAssets::RequestStat(const std::string& name) {
  if (!mutex_ || !IsAssetFilename(name)) return false;
  Lock lock(mutex_);
  if (!card_ || playbackReserved_ || active_ != Work::None || pending_ != Work::None || resultReady_ || statReady_) return false;
  name_ = name; pending_ = Work::Stat; status_.state = "waiting-idle"; return true;
}
bool EspIdfSdAssets::RequestUnmount() {
  if (!mutex_) return false;
  Lock lock(mutex_);
  if (!card_ || playbackReserved_ || active_ != Work::None || pending_ != Work::None) return false;
  pending_ = Work::Unmount; status_.state = "waiting-idle"; return true;
}
void EspIdfSdAssets::Tick(bool safeIdle) {
  if (!mutex_) return;
  bool notify = false;
  {
    Lock lock(mutex_);
    if (safeIdle && !playbackReserved_ && pending_ != Work::None && active_ == Work::None) {
      reserved_ = true; active_ = pending_; pending_ = Work::None;
      status_.state = active_ == Work::Mount ? "mounting" :
          active_ == Work::Read ? "reading" : active_ == Work::Stat ? "stat" : "unmounting";
      notify = true;
    }
  }
  if (notify) xTaskNotifyGive(task_);
}
bool EspIdfSdAssets::TakeReadResult(AssetBytes& output, std::string& error) {
  if (!mutex_) return false;
  Lock lock(mutex_);
  if (!resultReady_) return false;
  output = std::move(result_); error = std::move(resultError_);
  resultReady_ = false; return true;
}
bool EspIdfSdAssets::TakeStatResult(AssetInfo& info, std::string& error) {
  if (!mutex_) return false;
  Lock lock(mutex_);
  if (!statReady_) return false;
  info = resultInfo_; error = std::move(resultError_);
  statReady_ = false; return true;
}
void EspIdfSdAssets::WorkerEntry(void* owner) { static_cast<EspIdfSdAssets*>(owner)->Worker(); }
void EspIdfSdAssets::Worker() {
  for (;;) {
    ulTaskNotifyTake(pdTRUE, portMAX_DELAY);
    Work work;
    { Lock lock(mutex_); work = active_; }
    Execute(work);
  }
}
void EspIdfSdAssets::Execute(Work work) {
  std::string error;
  sdmmc_card_t* mounted = nullptr;
  bool unmounted = false;
  AssetBytes bytes;
  AssetInfo info;
  if (work == Work::Mount) {
    sdmmc_host_t host = SDMMC_HOST_DEFAULT();
    host.flags = SDMMC_HOST_FLAG_1BIT;
    host.max_freq_khz = SDMMC_FREQ_DEFAULT;
    host.command_timeout_ms = 1000;
    host.do_transaction = ReadOnlyTransaction;
    sdmmc_slot_config_t slot = SDMMC_SLOT_CONFIG_DEFAULT();
    slot.width = 1;
    slot.clk = GPIO_NUM_39; slot.cmd = GPIO_NUM_38; slot.d0 = GPIO_NUM_40;
    // No guessed pullups, card-detect or write-protect pins.
    slot.flags = 0;
    esp_vfs_fat_sdmmc_mount_config_t config{};
    config.format_if_mount_failed = false;
    config.max_files = 1;
    config.disk_status_check_enable = true;
    // ESP-IDF releases host/card/VFS allocations on any failed mount.
    if (esp_vfs_fat_sdmmc_mount(MountPath, &host, &slot, &config, &mounted) != ESP_OK) error = "sd-mount-failed";
  } else if (work == Work::Read) {
    FatFile file;
    ReadAsset(file, name_, maximumBytes_, bytes, error);
  } else if (work == Work::Stat) {
    FatFile file;
    if (!file.Open(name_, info)) error = "asset-open-failed";
    else if (!info.regular || info.symlink) error = "asset-not-regular";
  } else if (work == Work::Unmount) {
    // IDF consumes/frees the card even if VFS unregister returns an error.
    const esp_err_t result = esp_vfs_fat_sdcard_unmount(MountPath, static_cast<sdmmc_card_t*>(card_));
    unmounted = true;
    if (result != ESP_OK) error = "sd-unmount-failed";
  }
  Lock lock(mutex_);
  if (mounted && error.empty()) card_ = mounted;
  if (unmounted) card_ = nullptr;
  if (work == Work::Read) { result_ = std::move(bytes); resultError_ = error; resultReady_ = true; }
  if (work == Work::Stat) { resultInfo_ = info; resultError_ = error; statReady_ = true; }
  status_.error = error;
  status_.state = error.empty() ? (card_ ? "mounted" : "unmounted") : "error";
  active_ = Work::None; reserved_ = false;
}
} }

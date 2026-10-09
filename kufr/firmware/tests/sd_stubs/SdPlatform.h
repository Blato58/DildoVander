#pragma once
#include "../ota_stubs/OtaPlatform.h"
#include <sys/stat.h>
#if defined(_WIN32) && !defined(S_ISREG)
#define S_ISREG(mode) (((mode) & _S_IFMT) == _S_IFREG)
#endif
constexpr int ESP_ERR_NOT_SUPPORTED = -2;
constexpr uint32_t SDMMC_HOST_FLAG_1BIT = 1;
constexpr int SDMMC_FREQ_DEFAULT = 20000, SCF_CMD_READ = 0x40;
constexpr int GPIO_NUM_38 = 38, GPIO_NUM_39 = 39, GPIO_NUM_40 = 40;
struct sdmmc_command_t { uint32_t opcode = 0; size_t datalen = 0; int flags = 0; };
struct sdmmc_host_t {
  uint32_t flags = 0;
  int max_freq_khz = 0, command_timeout_ms = 0;
  int (*do_transaction)(int, sdmmc_command_t*) = nullptr;
};
struct sdmmc_slot_config_t { uint8_t width = 4; int clk = -1, cmd = -1, d0 = -1; uint32_t flags = 0; };
struct sdmmc_card_t {};
struct esp_vfs_fat_sdmmc_mount_config_t { bool format_if_mount_failed = true; int max_files = 0; bool disk_status_check_enable = false; };
#define SDMMC_HOST_DEFAULT() sdmmc_host_t{}
#define SDMMC_SLOT_CONFIG_DEFAULT() sdmmc_slot_config_t{}
namespace sd_fake {
extern bool mountSuccess, unmountSuccess, mounted;
extern unsigned mounts, unmounts, cleanups, transactions;
extern sdmmc_host_t configuredHost;
extern sdmmc_slot_config_t configuredSlot;
extern sdmmc_card_t card;
void Reset();
}
inline int sdmmc_host_do_transaction(int, sdmmc_command_t*) { ++sd_fake::transactions; return ESP_OK; }
inline int esp_vfs_fat_sdmmc_mount(const char*, const sdmmc_host_t* host, const sdmmc_slot_config_t* slot,
                                 const esp_vfs_fat_sdmmc_mount_config_t* config, sdmmc_card_t** card) {
  ++sd_fake::mounts;
  sd_fake::configuredHost = *host; sd_fake::configuredSlot = *slot;
  if (!sd_fake::mountSuccess || config->format_if_mount_failed || config->max_files != 1 || !config->disk_status_check_enable) {
    ++sd_fake::cleanups; return ESP_FAIL;
  }
  sd_fake::mounted = true; *card = &sd_fake::card; return ESP_OK;
}
inline int esp_vfs_fat_sdcard_unmount(const char*, sdmmc_card_t*) {
  ++sd_fake::unmounts; ++sd_fake::cleanups; sd_fake::mounted = false;
  return sd_fake::unmountSuccess ? ESP_OK : ESP_FAIL;
}

#include "sd_stubs/SdPlatform.h"
#include "../src/features/sd_assets/EspIdfSdAssets.h"
#include <cassert>
#include <iostream>

namespace ota_fake {
uint64_t nowMs = 0;
std::map<std::string, std::string> settings;
}
namespace sd_fake {
bool mountSuccess, unmountSuccess, mounted;
unsigned mounts, unmounts, cleanups, transactions;
sdmmc_host_t configuredHost;
sdmmc_slot_config_t configuredSlot;
sdmmc_card_t card;
void Reset() {
  ota_fake::settings = {{"profile_b_ok", "1"}, {"sd_verified", "1"}};
  mountSuccess = unmountSuccess = true; mounted = false;
  mounts = unmounts = cleanups = transactions = 0;
}
}
namespace kufr { namespace assets {
struct SdAssetsTestAccess { static void Run(EspIdfSdAssets& sd) { sd.Execute(sd.active_); } };
} }
using namespace kufr::assets;
int main() {
  sd_fake::Reset(); ota_fake::settings.erase("sd_verified");
  EspIdfSdAssets disabled;
  assert(disabled.Begin() && disabled.Status().state == "disabled" && !disabled.RequestMount());
  disabled.Tick(true); assert(sd_fake::mounts == 0);
  sd_fake::Reset();
  EspIdfSdAssets sd;
  assert(sd.Begin()); sd.Tick(false); assert(!sd.Reserved() && sd_fake::mounts == 0);
  sd.Tick(true); assert(sd.Reserved() && sd.Status().state == "mounting");
  SdAssetsTestAccess::Run(sd);
  assert(sd.Status().state == "mounted" && !sd.Reserved() && sd_fake::mounted);
  assert(sd_fake::configuredSlot.width == 1 && sd_fake::configuredSlot.cmd == 38 &&
      sd_fake::configuredSlot.clk == 39 && sd_fake::configuredSlot.d0 == 40 && sd_fake::configuredSlot.flags == 0);
  assert(sd_fake::configuredHost.flags == SDMMC_HOST_FLAG_1BIT && sd_fake::configuredHost.command_timeout_ms == 1000);
  assert(!sd.RequestMount() && !sd.RequestRead("../secret", 16) && !sd.RequestRead("asset.bin", MaximumAssetBytes + 1));
  sdmmc_command_t read{17, 512, SCF_CMD_READ};
  assert(sd_fake::configuredHost.do_transaction(1, &read) == ESP_OK && sd_fake::transactions == 1);
  sdmmc_command_t write{24, 512, 0};
  assert(sd_fake::configuredHost.do_transaction(1, &write) == ESP_ERR_NOT_SUPPORTED && sd_fake::transactions == 1);
  sdmmc_command_t erase{38, 0, 0};
  assert(sd_fake::configuredHost.do_transaction(1, &erase) == ESP_ERR_NOT_SUPPORTED);
  sdmmc_command_t outbound{42, 512, 0};
  assert(sd_fake::configuredHost.do_transaction(1, &outbound) == ESP_ERR_NOT_SUPPORTED);
  assert(sd.RequestRead("missing-asset.bin", 16));
  sd.Tick(false); assert(!sd.Reserved());
  sd.Tick(true); SdAssetsTestAccess::Run(sd);
  AssetBytes bytes; std::string error;
  assert(sd.TakeReadResult(bytes, error) && bytes.size == 0 && error == "asset-open-failed");
  assert(!sd.TakeReadResult(bytes, error));
  assert(!sd.RequestStat("../secret"));
  assert(sd.RequestStat("missing-asset.bin"));
  sd.Tick(true); SdAssetsTestAccess::Run(sd);
  AssetInfo info;
  assert(!sd.RequestRead("other.bin", 16));  // Consume the outstanding stat first.
  assert(sd.TakeStatResult(info, error) && error == "asset-open-failed");
  assert(!sd.TakeStatResult(info, error));
  assert(sd.RequestUnmount()); sd.Tick(true); SdAssetsTestAccess::Run(sd);
  assert(sd.Status().state == "unmounted" && !sd_fake::mounted && sd_fake::unmounts == 1 && sd_fake::cleanups == 1);
  sd_fake::Reset(); sd_fake::mountSuccess = false;
  EspIdfSdAssets failed;
  assert(failed.Begin()); failed.Tick(true); SdAssetsTestAccess::Run(failed);
  assert(failed.Status().state == "error" && !failed.Reserved() && !failed.RequestUnmount() && sd_fake::cleanups == 1);
  assert(failed.RequestMount()); sd_fake::mountSuccess = true;
  failed.Tick(true); SdAssetsTestAccess::Run(failed);
  assert(failed.Status().state == "mounted");
  sd_fake::unmountSuccess = false;
  assert(failed.RequestUnmount()); failed.Tick(true); SdAssetsTestAccess::Run(failed);
  assert(failed.Status().error == "sd-unmount-failed" && !failed.Reserved() && !failed.RequestUnmount());
  std::cout << "ESP-IDF SD mount gate, pins, read-only transactions, idle reservation and cleanup tests passed (fake SDMMC).\n";
}

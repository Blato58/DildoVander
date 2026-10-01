#include "EspIdfStationNetwork.h"

#include "esp_log.h"
#include "esp_random.h"

#include "EspIdfNetworkSettings.h"
#include "ReconnectPolicy.h"

namespace {
constexpr char LogTag[] = "kufr_station";
}

bool EspIdfStationNetwork::Start(bool nvsInitialized) {
  if (startRequested_) {
    return false;
  }
  startRequested_ = true;
  if (LoadEspIdfNetworkSettings(settings_, nvsInitialized) !=
      NetworkSettingsLoadResult::Loaded) {
    return false;
  }
  if (xTaskCreate(TaskEntry, "station_network", 4096, this, 4, &task_) != pdPASS) {
    settings_.Reset();
    task_ = nullptr;
    return false;
  }
  return true;
}

bool EspIdfStationNetwork::HasIp() const {
  // Publish the event-group lifetime only after Start has initialized it.
  return initialized_.load() && wifi_.HasIp();
}

const NetworkSettings& EspIdfStationNetwork::Settings() const { return settings_; }

uint32_t EspIdfStationNetwork::IpGeneration() const {
  return initialized_.load() ? wifi_.IpGeneration() : 0;
}

void EspIdfStationNetwork::TaskEntry(void* context) {
  auto* network = static_cast<EspIdfStationNetwork*>(context);
  network->Run();
  vTaskDelete(nullptr);
}

void EspIdfStationNetwork::Run() {
  if (!wifi_.Start(settings_)) {
    ESP_LOGE(LogTag, "STA initialization failed; local/cloud network offline");
    return;
  }
  initialized_.store(true);
  ReconnectPolicy reconnect;
  while (true) {
    if (wifi_.HasIp()) {
      vTaskDelay(pdMS_TO_TICKS(100));
    } else if (wifi_.ConnectAndWait(15000)) {
      reconnect.Reset();
    } else {
      vTaskDelay(pdMS_TO_TICKS(reconnect.NextDelayMs(esp_random() % 401)));
    }
  }
}

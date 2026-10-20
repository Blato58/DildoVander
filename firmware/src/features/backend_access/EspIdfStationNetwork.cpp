#include "EspIdfStationNetwork.h"

#include <cstring>
#include "esp_log.h"
#include "esp_random.h"
#include "esp_timer.h"
#include "EspIdfNetworkSettings.h"
#include "ReconnectPolicy.h"

bool EspIdfStationNetwork::Start(bool nvsInitialized) {
  if (startRequested_ || !nvsInitialized) { return false; }
  startRequested_ = true;
  lock_ = xSemaphoreCreateMutex();
  if (lock_ == nullptr) { return false; }
  const bool legacyLoaded = LoadEspIdfNetworkSettings(settings_, true) == NetworkSettingsLoadResult::Loaded;
  NetworkProfileRecord record;
  bool missing;
  if (!storage_.Load(record, missing)) {
    // A damaged profile must remain recoverable through authenticated BLE.
    // Do not overwrite flash until a replacement has passed its connection test.
    NetworkProfiles::Wipe(&record, sizeof(record));
    record = NetworkProfileRecord{};
    missing = false;
    ESP_LOGE("kufr_station", "Stored network profiles invalid; authenticated recovery required");
  }
  if (missing && legacyLoaded && settings_.Ssid()[0] != 0) {
    NetworkProfiles::MakeCandidate(settings_.Ssid(), settings_.Password(), record.hotspot);
    storage_.Save(record);
  }
  const bool loaded = profiles_.Load(record);
  NetworkProfiles::Wipe(&record, sizeof(record));
  if (!loaded) { return false; }
  Publish("offline", "idle");
  if (xTaskCreate(TaskEntry, "station_network", 6144, this, 4, &task_) != pdPASS) {
    task_ = nullptr;
    return false;
  }
  return true;
}
void EspIdfStationNetwork::SetChangeGate(NetworkChangeGate gate) { gate_ = gate; }
bool EspIdfStationNetwork::HasIp() const { return initialized_.load() && wifi_.HasIp(); }
uint32_t EspIdfStationNetwork::IpGeneration() const { return initialized_.load() ? wifi_.IpGeneration() : 0; }
std::string EspIdfStationNetwork::CurrentIpv4() const {
  const uint32_t generation = IpGeneration();
  if (!initialized_.load() || changing_.load() || !wifi_.HasIp()) { return {}; }
  const std::string address = wifi_.CurrentIpv4();
  return !changing_.load() && wifi_.HasIp() && IpGeneration() == generation ? address : "";
}
const NetworkSettings& EspIdfStationNetwork::Settings() const { return settings_; }
StationNetworkStatus EspIdfStationNetwork::Status() {
  StationNetworkStatus result;
  if (lock_ != nullptr && xSemaphoreTake(lock_, pdMS_TO_TICKS(50)) == pdTRUE) {
    result = status_;
    xSemaphoreGive(lock_);
  }
  return result;
}
void EspIdfStationNetwork::Publish(const char* phase, const char* result) {
  xSemaphoreTake(lock_, portMAX_DELAY);
  std::strcpy(status_.hotspotSsid, profiles_.Record().hotspot.ssid);
  std::strcpy(status_.fallbackSsid, profiles_.Record().fallback.ssid);
  status_.active = active_;
  status_.lastSuccessful = profiles_.Record().lastSuccessful;
  status_.phase = phase;
  status_.result = result;
  xSemaphoreGive(lock_);
}
bool EspIdfStationNetwork::Queue(uint8_t operation, NetworkProfile profile, const WifiProfile* candidate) {
  if (profile != NetworkProfile::Hotspot && profile != NetworkProfile::Fallback) { return false; }
  if (lock_ == nullptr || xSemaphoreTake(lock_, pdMS_TO_TICKS(50)) != pdTRUE) { return false; }
  const bool accepted = operation_ == 0 && !changing_.load();
  if (accepted) {
    requestedProfile_ = profile;
    if (candidate != nullptr) { candidate_ = *candidate; }
    operation_ = operation;
    status_.result = "accepted";
  }
  xSemaphoreGive(lock_);
  return accepted;
}
bool EspIdfStationNetwork::Configure(NetworkProfile profile, const char* ssid, const char* password) {
  WifiProfile candidate;
  const bool valid = NetworkProfiles::MakeCandidate(ssid, password, candidate);
  const bool accepted = valid && Queue(1, profile, &candidate);
  NetworkProfiles::Wipe(&candidate, sizeof(candidate));
  return accepted;
}
bool EspIdfStationNetwork::Delete(NetworkProfile profile) { return Queue(2, profile, nullptr); }
bool EspIdfStationNetwork::Test(NetworkProfile profile) { return Queue(3, profile, nullptr); }
bool EspIdfStationNetwork::Connect(NetworkProfile profile, const WifiProfile& config) {
  active_ = profile;
  Publish("connecting", "accepted");
  return wifi_.Select(config.ssid, config.password) && wifi_.ConnectAndWait(15000);
}
void EspIdfStationNetwork::ProcessRequest() {
  xSemaphoreTake(lock_, portMAX_DELAY);
  const uint8_t operation = operation_;
  const NetworkProfile profile = requestedProfile_;
  WifiProfile candidate = candidate_;
  if (operation != 0) { changing_.store(true); }
  xSemaphoreGive(lock_);
  if (operation == 0) { NetworkProfiles::Wipe(&candidate, sizeof(candidate)); return; }
  const NetworkProfile previous = active_;
  const bool acquired = gate_.acquire != nullptr && gate_.release != nullptr && gate_.acquire(gate_.context);
  const char* result = "busy";
  if (acquired) {
    if (operation == 2) {
      result = profiles_.Delete(profile) ? "deleted" : "storage_failed";
      if (profile == active_ && std::strcmp(result, "deleted") == 0) {
        wifi_.Disconnect();
        active_ = NetworkProfile::None;
      }
    } else {
      const auto* target = operation == 1 ? &candidate : profiles_.Get(profile);
      if (target == nullptr || !target->Configured()) {
        result = "invalid_request";
      } else if (Connect(profile, *target)) {
        const bool saved = operation == 1 ? profiles_.CommitReplacement(profile, candidate) : profiles_.MarkSuccessful(profile);
        result = saved ? "connected" : "storage_failed";
      } else {
        result = "connection_failed";
      }
      if (std::strcmp(result, "connected") != 0) {
        wifi_.Disconnect();
        active_ = NetworkProfile::None;
        const auto* old = profiles_.Get(previous);
        if (old != nullptr && old->Configured() && Connect(previous, *old)) { active_ = previous; }
        else { active_ = NetworkProfile::None; }
      }
    }
    gate_.release(gate_.context);
  }
  NetworkProfiles::Wipe(&candidate, sizeof(candidate));
  Publish(wifi_.HasIp() ? "connected" : "offline", result);
  xSemaphoreTake(lock_, portMAX_DELAY);
  NetworkProfiles::Wipe(&candidate_, sizeof(candidate_));
  operation_ = 0;
  changing_.store(false);
  xSemaphoreGive(lock_);
}
void EspIdfStationNetwork::TaskEntry(void* context) {
  static_cast<EspIdfStationNetwork*>(context)->Run();
  vTaskDelete(nullptr);
}
void EspIdfStationNetwork::Run() {
  if (!wifi_.Start()) { ESP_LOGE("kufr_station", "STA initialization failed"); return; }
  initialized_.store(true);
  ReconnectPolicy reconnect;
  int64_t nextAttempt = 0;
  int64_t nextPreferredProbe = 0;
  NetworkProfile attempt = profiles_.First();
  bool wasConnected = false;
  while (true) {
    const uint32_t generationBeforeRequest = wifi_.IpGeneration();
    ProcessRequest();
    const int64_t now = esp_timer_get_time() / 1000;
    if (wifi_.HasIp() && active_ == NetworkProfile::Fallback &&
        wifi_.IpGeneration() != generationBeforeRequest) {
      // Explicit fallback tests/configuration and successful rollbacks deserve
      // a full quiet interval before probing the preferred network again.
      nextPreferredProbe = now + 60000;
    }
    if (wasConnected && !wifi_.HasIp()) { attempt = profiles_.First(); }
    wasConnected = wifi_.HasIp();
    const auto* attempted = profiles_.Get(attempt);
    if (attempted == nullptr || !attempted->Configured()) { attempt = profiles_.First(); }
    const bool probe = wifi_.HasIp() && active_ == NetworkProfile::Fallback &&
        profiles_.Get(NetworkProfile::Hotspot)->Configured() && now >= nextPreferredProbe;
    if ((!wifi_.HasIp() && now >= nextAttempt) || probe) {
      if (attempt == NetworkProfile::None || probe) { attempt = profiles_.First(); }
      const auto* target = profiles_.Get(attempt);
      if (target != nullptr && target->Configured() && gate_.acquire != nullptr &&
          gate_.release != nullptr && gate_.acquire(gate_.context)) {
        changing_.store(true);
        const NetworkProfile previous = active_;
        if (Connect(attempt, *target)) {
          profiles_.MarkSuccessful(attempt);
          reconnect.Reset();
          Publish("connected", "connected");
        } else {
          wifi_.Disconnect();
          active_ = NetworkProfile::None;
          if (probe) {
            const auto* old = profiles_.Get(previous);
            if (old != nullptr && Connect(previous, *old)) { active_ = previous; }
          }
          const auto other = profiles_.Other(attempt);
          attempt = other == NetworkProfile::None ? profiles_.First() : other;
          Publish(wifi_.HasIp() ? "connected" : "offline", "connection_failed");
        }
        changing_.store(false);
        gate_.release(gate_.context);
        const int64_t completedAt = esp_timer_get_time() / 1000;
        nextAttempt = completedAt + reconnect.NextDelayMs(esp_random() % 401);
        nextPreferredProbe = completedAt + 60000;
      }
    }
    if (!wifi_.HasIp() && !changing_.load()) {
      xSemaphoreTake(lock_, portMAX_DELAY);
      status_.phase = "offline";
      status_.active = NetworkProfile::None;
      xSemaphoreGive(lock_);
    }
    vTaskDelay(pdMS_TO_TICKS(100));
  }
}

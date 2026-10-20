#include "EspIdfPairingRandom.h"

#include "esp_random.h"
#include "esp_wifi.h"

namespace {

bool HasRadioEntropy() {
  wifi_ap_record_t accessPoint{};
  return esp_wifi_sta_get_ap_info(&accessPoint) == ESP_OK;
}

void Wipe(uint8_t* output, size_t length) {
  volatile uint8_t* bytes = output;
  for (size_t index = 0; index < length; ++index) {
    bytes[index] = 0;
  }
}

}  // namespace

bool EspIdfPairingRandom::Fill(uint8_t* output, size_t length) {
  if (output == nullptr || length != LocalPairingCredential::TokenSize) {
    return false;
  }
  Wipe(output, length);
  // No ADC entropy toggling while Wi-Fi or future I2S tasks are running.
  if (!HasRadioEntropy()) {
    return false;
  }
  esp_fill_random(output, length);
  if (!HasRadioEntropy()) {
    Wipe(output, length);
    return false;
  }
  return true;
}

#include "NetworkSettings.h"

#include <algorithm>
#include <cstring>

namespace {

template <size_t Length>
void CopyText(const char* source, size_t length,
              std::array<char, Length>& target) {
  std::copy(source, source + length, target.begin());
  target[length] = '\0';
}

template <size_t Length>
void Wipe(std::array<char, Length>& target) {
  volatile char* bytes = target.data();
  for (size_t index = 0; index < target.size(); ++index) {
    bytes[index] = 0;
  }
}

}  // namespace

NetworkSettings::~NetworkSettings() { Clear(); }

void NetworkSettings::Reset() { Clear(); }

bool NetworkSettings::Configure(const char* ssid, const char* password,
                                const char* backendHost) {
  Clear();
  if (ssid == nullptr || password == nullptr || backendHost == nullptr) {
    return false;
  }
  const size_t ssidLength = std::strlen(ssid);
  const size_t passwordLength = std::strlen(password);
  const size_t hostLength = std::strlen(backendHost);
  if (ssidLength == 0 || ssidLength > 32 || passwordLength < 8 ||
      passwordLength > 63 || hostLength > 253 ||
      !IsValidBackendHost(backendHost)) {
    return false;
  }
  for (size_t index = 0; index < ssidLength; ++index) {
    const unsigned char value = static_cast<unsigned char>(ssid[index]);
    if (value < 0x20) {
      return false;
    }
  }
  for (size_t index = 0; index < passwordLength; ++index) {
    const unsigned char value = static_cast<unsigned char>(password[index]);
    if (value < 0x20 || value > 0x7e) {
      return false;
    }
  }

  CopyText(ssid, ssidLength, ssid_);
  CopyText(password, passwordLength, password_);
  CopyText(backendHost, hostLength, backendHost_);
  configured_ = true;
  return true;
}

bool NetworkSettings::IsConfigured() const { return configured_; }

const char* NetworkSettings::Ssid() const {
  return configured_ ? ssid_.data() : nullptr;
}

const char* NetworkSettings::Password() const {
  return configured_ ? password_.data() : nullptr;
}

const char* NetworkSettings::BackendHost() const {
  return configured_ ? backendHost_.data() : nullptr;
}

bool NetworkSettings::IsValidBackendHost(const std::string& host) {
  if (host.empty() || host.size() > 253 || host.front() == '.' ||
      host.back() == '.') {
    return false;
  }

  size_t labelLength = 0;
  for (size_t index = 0; index < host.size(); ++index) {
    const char value = host[index];
    if (value == '.') {
      if (labelLength == 0 || labelLength > 63 || host[index - 1] == '-') {
        return false;
      }
      labelLength = 0;
      continue;
    }
    if (!((value >= 'a' && value <= 'z') ||
          (value >= 'A' && value <= 'Z') ||
          (value >= '0' && value <= '9') || value == '-')) {
      return false;
    }
    if (labelLength == 0 && value == '-') {
      return false;
    }
    ++labelLength;
  }
  return labelLength > 0 && labelLength <= 63 && host.back() != '-';
}

void NetworkSettings::Clear() {
  Wipe(ssid_);
  Wipe(password_);
  Wipe(backendHost_);
  configured_ = false;
}

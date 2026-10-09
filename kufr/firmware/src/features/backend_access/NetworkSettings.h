#pragma once

#include <stddef.h>

#include <array>
#include <string>

class NetworkSettings final {
 public:
  NetworkSettings() = default;
  ~NetworkSettings();
  NetworkSettings(const NetworkSettings&) = delete;
  NetworkSettings& operator=(const NetworkSettings&) = delete;

  bool Configure(const char* ssid, const char* password,
                 const char* backendHost);
  bool ConfigureBackend(const char* backendHost);
  void Reset();
  bool IsConfigured() const;
  const char* Ssid() const;
  const char* Password() const;
  const char* BackendHost() const;
  static bool IsValidBackendHost(const std::string& host);

 private:
  void Clear();

  std::array<char, 33> ssid_{};
  std::array<char, 64> password_{};
  std::array<char, 254> backendHost_{};
  bool configured_ = false;
};

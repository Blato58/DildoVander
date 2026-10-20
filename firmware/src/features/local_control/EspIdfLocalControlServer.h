#pragma once

#include <array>
#include <mutex>

#include "esp_https_server.h"

#include "LocalControlTransport.h"

class LocalTlsIdentity;
class LocalPairingCredential;
class LocalControlInbox;
class ShowTraceReadPort;

// Start/Stop/Pump belong to the control owner. Callbacks only validate/enqueue;
// they never invoke the control runtime or any actuator port.
class EspIdfLocalControlServer final : public LocalControlTransport {
 public:
  static constexpr size_t MaximumFrameSize = 8192;
  static constexpr size_t OutputCapacity = 4;

  EspIdfLocalControlServer(LocalTlsIdentity& identity,
                          LocalPairingCredential& credential,
                          LocalSocketAdmission& admission,
                          LocalControlInbox& inbox);
  ~EspIdfLocalControlServer();
  EspIdfLocalControlServer(const EspIdfLocalControlServer&) = delete;
  EspIdfLocalControlServer& operator=(const EspIdfLocalControlServer&) = delete;

  // Wi-Fi/netif lifecycle and service provisioning are owned elsewhere.
  bool Start();
  // Bind at startup; callbacks only read a published observer snapshot.
  void BindShowTrace(ShowTraceReadPort& trace) { showTrace_ = &trace; }
  void Stop();
  void Pump();
  bool Send(const LocalSocketAdmission::Handle& handle,
            const std::string& json) override;
  void Close(const LocalSocketAdmission::Handle& handle) override;

 private:
  struct Session {
    EspIdfLocalControlServer* server;
    LocalSocketAdmission::Handle handle;
  };
  struct Output {
    LocalSocketAdmission::Handle handle;
    std::string json;
  };

  static esp_err_t BeforeUpgrade(httpd_req_t* request);
  static esp_err_t AfterUpgrade(httpd_req_t* request);
  static esp_err_t Receive(httpd_req_t* request);
  static esp_err_t Pair(httpd_req_t* request);
  static esp_err_t ShowTrace(httpd_req_t* request);
  static void FreeSession(void* context);
  static void Work(void* context);
  bool MatchesSession(const LocalSocketAdmission::Handle& handle) const;
  void CloseOnServer(const LocalSocketAdmission::Handle& handle);
  void ClearOutput();

  LocalTlsIdentity& identity_;
  LocalPairingCredential& credential_;
  LocalSocketAdmission& admission_;
  LocalControlInbox& inbox_;
  ShowTraceReadPort* showTrace_ = nullptr;
  httpd_handle_t server_ = nullptr;
  std::mutex mutex_;
  std::array<Output, OutputCapacity> output_;
  size_t head_ = 0;
  size_t count_ = 0;
  bool workScheduled_ = false;
  LocalSocketAdmission::Handle closing_;
};

#include "EspIdfLocalControlServer.h"

#include <cstring>
#include <memory>
#include <new>

#include "esp_timer.h"
#include "lwip/sockets.h"

#include "LocalControlCodec.h"
#include "LocalControlInbox.h"
#include "../provisioning/LocalPairingCredential.h"
#include "../provisioning/LocalTlsIdentity.h"

namespace {

uint64_t NowMs() {
  return static_cast<uint64_t>(esp_timer_get_time() / 1000);
}

void Wipe(char* bytes, size_t length) {
  volatile char* target = bytes;
  for (size_t index = 0; index < length; ++index) {
    target[index] = 0;
  }
}

void Wipe(std::string& text) {
  if (!text.empty()) {
    Wipe(&text[0], text.size());
    text.clear();
  }
}

bool SameHandle(const LocalSocketAdmission::Handle& first,
                const LocalSocketAdmission::Handle& second) {
  return first.socket == second.socket && first.generation == second.generation;
}

esp_err_t Reject(httpd_req_t* request, const char* status) {
  httpd_resp_set_status(request, status);
  httpd_resp_send(request, "", 0);
  return ESP_FAIL;
}

}  // namespace

EspIdfLocalControlServer::EspIdfLocalControlServer(
    LocalTlsIdentity& identity, LocalPairingCredential& credential,
    LocalSocketAdmission& admission, LocalControlInbox& inbox)
    : identity_(identity), credential_(credential), admission_(admission),
      inbox_(inbox) {}

EspIdfLocalControlServer::~EspIdfLocalControlServer() { Stop(); }

bool EspIdfLocalControlServer::Start() {
  if (server_ != nullptr || !identity_.IsHealthy() ||
      !identity_.IsProvisioned() || !credential_.IsHealthy() ||
      !credential_.IsPaired()) {
    return false;
  }
  // IDF 6.1's vendor macro omits the deprecated use_secure_element field.
  // Keep its remaining defaults and value-initialized false without relaxing
  // warnings for our own initializers or the rest of the translation unit.
#if defined(__GNUC__)
#pragma GCC diagnostic push
#pragma GCC diagnostic ignored "-Wmissing-field-initializers"
#endif
  httpd_ssl_config_t config = HTTPD_SSL_CONFIG_DEFAULT();
#if defined(__GNUC__)
#pragma GCC diagnostic pop
#endif
  config.transport_mode = HTTPD_SSL_TRANSPORT_SECURE;
  config.port_secure = 443;
  config.servercert = identity_.Certificate();
  config.servercert_len = identity_.CertificateLength();
  config.prvtkey_pem = identity_.PrivateKey();
  config.prvtkey_len = identity_.PrivateKeyLength();
  config.tls_handshake_timeout_ms = 5000;
  config.httpd.max_open_sockets = 2;
  config.httpd.max_uri_handlers = 1;
  config.httpd.lru_purge_enable = false;
  config.httpd.recv_wait_timeout = 1;
  config.httpd.send_wait_timeout = 1;
  config.httpd.max_req_hdr_len = 1024;
  config.httpd.max_uri_len = 64;
  if (httpd_ssl_start(&server_, &config) != ESP_OK) {
    server_ = nullptr;
    return false;
  }
  httpd_uri_t endpoint = {};
  endpoint.uri = "/v1/control";
  endpoint.method = HTTP_GET;
  endpoint.handler = Receive;
  endpoint.user_ctx = this;
  endpoint.is_websocket = true;
  endpoint.handle_ws_control_frames = true;
  endpoint.ws_pre_handshake_cb = BeforeUpgrade;
  endpoint.ws_post_handshake_cb = AfterUpgrade;
  if (httpd_register_uri_handler(server_, &endpoint) != ESP_OK) {
    Stop();
    return false;
  }
  return true;
}

void EspIdfLocalControlServer::Stop() {
  if (server_ == nullptr) {
    return;
  }
  // httpd_ssl_stop waits for the server task and its session destructors.
  // Do not hold the output mutex while waiting for queued work to finish.
  httpd_ssl_stop(server_);
  server_ = nullptr;
  std::lock_guard<std::mutex> lock(mutex_);
  ClearOutput();
  closing_ = {};
  workScheduled_ = false;
}

esp_err_t EspIdfLocalControlServer::BeforeUpgrade(httpd_req_t* request) {
  auto* server = static_cast<EspIdfLocalControlServer*>(request->user_ctx);
  if (std::strcmp(request->uri, "/v1/control") != 0 ||
      httpd_req_get_hdr_value_len(request, "Sec-WebSocket-Extensions") != 0 ||
      request->sess_ctx != nullptr) {
    return Reject(request, "400 Bad Request");
  }
  constexpr size_t HeaderLength = 71;
  char authorization[HeaderLength + 1] = {};
  const bool headerValid =
      httpd_req_get_hdr_value_len(request, "Authorization") == HeaderLength &&
      httpd_req_get_hdr_value_str(request, "Authorization", authorization,
                                 sizeof(authorization)) == ESP_OK;
  LocalSocketAdmission::Handle handle;
  const LocalSocketAdmission::ReserveResult admission = headerValid
      ? server->admission_.TryReserve(httpd_req_to_sockfd(request), authorization,
                                      HeaderLength, NowMs(), handle)
      : LocalSocketAdmission::ReserveResult::Unauthorized;
  Wipe(authorization, sizeof(authorization));
  if (admission != LocalSocketAdmission::ReserveResult::Accepted) {
    return Reject(request,
                  admission == LocalSocketAdmission::ReserveResult::Busy
                      ? "409 Conflict" : "401 Unauthorized");
  }
  auto* session = new (std::nothrow) Session{server, handle};
  if (session == nullptr) {
    server->admission_.Release(handle);
    return Reject(request, "503 Service Unavailable");
  }
  request->sess_ctx = session;
  request->free_ctx = FreeSession;
  return ESP_OK;
}

esp_err_t EspIdfLocalControlServer::AfterUpgrade(httpd_req_t* request) {
  auto* session = static_cast<Session*>(request->sess_ctx);
  if (session == nullptr ||
      !session->server->admission_.Confirm(session->handle, NowMs()) ||
      !session->server->inbox_.Open(session->handle, NowMs())) {
    return ESP_FAIL;
  }
  return ESP_OK;
}

void EspIdfLocalControlServer::FreeSession(void* context) {
  auto* session = static_cast<Session*>(context);
  if (session == nullptr) {
    return;
  }
  session->server->admission_.Release(session->handle);
  session->server->inbox_.Disconnect(session->handle);
  delete session;
}

esp_err_t EspIdfLocalControlServer::Receive(httpd_req_t* request) {
  auto* session = static_cast<Session*>(request->sess_ctx);
  if (session == nullptr ||
      !session->server->admission_.IsActive(session->handle)) {
    return ESP_FAIL;
  }
  httpd_ws_frame_t frame = {};
  if (httpd_ws_recv_frame(request, &frame, 0) != ESP_OK || !frame.final) {
    return ESP_FAIL;
  }
  const bool control = frame.type == HTTPD_WS_TYPE_PING ||
                       frame.type == HTTPD_WS_TYPE_PONG ||
                       frame.type == HTTPD_WS_TYPE_CLOSE;
  if ((control && frame.len > 125) ||
      (!control && (frame.type != HTTPD_WS_TYPE_TEXT || frame.len == 0 ||
                    frame.len > MaximumFrameSize))) {
    return ESP_FAIL;
  }
  std::unique_ptr<uint8_t[]> payload(
      new (std::nothrow) uint8_t[frame.len + 1]);
  if (!payload) {
    return ESP_FAIL;
  }
  frame.payload = payload.get();
  const size_t payloadLength = frame.len;
  if (frame.len > 0 &&
      httpd_ws_recv_frame(request, &frame, frame.len) != ESP_OK) {
    Wipe(reinterpret_cast<char*>(payload.get()), frame.len);
    return ESP_FAIL;
  }
  esp_err_t result = ESP_OK;
  if (frame.type == HTTPD_WS_TYPE_PING) {
    frame.type = HTTPD_WS_TYPE_PONG;
    result = httpd_ws_send_frame(request, &frame);
  } else if (frame.type == HTTPD_WS_TYPE_CLOSE) {
    // Returning failure makes HTTPD close TLS and run the immutable context's
    // destructor. Notify the owner immediately, even if the close reply fails.
    session->server->inbox_.Disconnect(session->handle);
    frame.len = 0;
    httpd_ws_send_frame(request, &frame);
    result = ESP_FAIL;
  } else if (frame.type == HTTPD_WS_TYPE_TEXT) {
    LocalControlMessage message;
    if (!ParseLocalControlMessage(
            reinterpret_cast<const char*>(payload.get()), frame.len, message) ||
        !session->server->inbox_.Submit(session->handle, message)) {
      result = ESP_FAIL;
    }
    Wipe(message.text);
  }
  Wipe(reinterpret_cast<char*>(payload.get()), payloadLength);
  return result;
}

bool EspIdfLocalControlServer::Send(
    const LocalSocketAdmission::Handle& handle, const std::string& json) {
  if (json.empty() || json.size() > MaximumFrameSize ||
      !admission_.IsActive(handle)) {
    return false;
  }
  bool accepted = false;
  {
    std::lock_guard<std::mutex> lock(mutex_);
    if (server_ != nullptr && closing_.socket < 0 && count_ < OutputCapacity) {
      Output& output = output_[(head_ + count_) % OutputCapacity];
      output.handle = handle;
      output.json = json;
      ++count_;
      accepted = true;
    }
  }
  if (!accepted) {
    Close(handle);
  } else {
    Pump();
  }
  return accepted;
}

void EspIdfLocalControlServer::Close(
    const LocalSocketAdmission::Handle& handle) {
  if (!admission_.IsActive(handle)) {
    return;
  }
  inbox_.Disconnect(handle);
  {
    std::lock_guard<std::mutex> lock(mutex_);
    closing_ = handle;
    ClearOutput();
  }
  Pump();
}

void EspIdfLocalControlServer::Pump() {
  std::lock_guard<std::mutex> lock(mutex_);
  if (server_ != nullptr && !workScheduled_ &&
      (closing_.socket >= 0 || count_ > 0)) {
    workScheduled_ = true;
    if (httpd_queue_work(server_, Work, this) != ESP_OK) {
      // Keep bounded work, including close, for the next owner-loop retry.
      workScheduled_ = false;
    }
  }
}

bool EspIdfLocalControlServer::MatchesSession(
    const LocalSocketAdmission::Handle& handle) const {
  auto* session = static_cast<Session*>(
      httpd_sess_get_ctx(server_, handle.socket));
  return session != nullptr && session->server == this &&
         SameHandle(session->handle, handle);
}

void EspIdfLocalControlServer::CloseOnServer(
    const LocalSocketAdmission::Handle& handle) {
  if (MatchesSession(handle)) {
    // Same server task checks generation and shuts down the FD before it can
    // be recycled. HTTPS retains ownership of TLS/socket destruction.
    shutdown(handle.socket, SHUT_RDWR);
  }
}

void EspIdfLocalControlServer::Work(void* context) {
  auto* server = static_cast<EspIdfLocalControlServer*>(context);
  for (size_t index = 0; index < OutputCapacity; ++index) {
    Output output;
    LocalSocketAdmission::Handle closing;
    {
      std::lock_guard<std::mutex> lock(server->mutex_);
      closing = server->closing_;
      server->closing_ = {};
      if (closing.socket < 0 && server->count_ > 0) {
        Output& queued = server->output_[server->head_];
        output.handle = queued.handle;
        output.json = queued.json;
        Wipe(queued.json);
        queued = {};
        server->head_ = (server->head_ + 1) % OutputCapacity;
        --server->count_;
      }
    }
    if (closing.socket >= 0) {
      server->CloseOnServer(closing);
      break;
    }
    if (output.handle.socket < 0) {
      break;
    }
    if (server->admission_.IsActive(output.handle) &&
        server->MatchesSession(output.handle)) {
      httpd_ws_frame_t frame = {};
      frame.type = HTTPD_WS_TYPE_TEXT;
      frame.len = output.json.size();
      frame.payload = reinterpret_cast<uint8_t*>(&output.json[0]);
      if (httpd_ws_send_frame_async(server->server_, output.handle.socket,
                                     &frame) != ESP_OK) {
        server->inbox_.Disconnect(output.handle);
        server->CloseOnServer(output.handle);
      }
    }
    Wipe(output.json);
  }
  {
    std::lock_guard<std::mutex> lock(server->mutex_);
    server->workScheduled_ = false;
  }
  server->Pump();
}

void EspIdfLocalControlServer::ClearOutput() {
  for (Output& output : output_) {
    Wipe(output.json);
    output = {};
  }
  head_ = 0;
  count_ = 0;
}

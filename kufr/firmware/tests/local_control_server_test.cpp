#include <cassert>
#include <cstring>
#include <deque>
#include <fstream>
#include <iterator>
#include <map>
#include <string>

#include "../src/features/local_control/EspIdfLocalControlServer.h"
#include "../src/features/local_control/LocalBearerAuthenticator.h"
#include "../src/features/local_control/LocalControlInbox.h"
#include "../src/features/show_diagnostics/ShowTracePort.h"
#include "../src/features/provisioning/LocalPairingCredential.h"
#include "../src/features/provisioning/LocalTlsIdentity.h"

namespace {

httpd_uri_t endpoint{};
httpd_uri_t pairingEndpoint{};
httpd_uri_t traceEndpoint{};
httpd_ssl_config_t configuration{};
std::string authorization;
std::string extensions;
std::string status;
std::string incoming;
std::string contentType = "application/json";
std::string transferEncoding;
std::string responseBody;
size_t readOffset = 0;
httpd_ws_type_t incomingType = HTTPD_WS_TYPE_TEXT;
bool finalFrame = true;
bool failQueue = false;
bool failSend = false;
bool failStart = false;
bool failRegister = false;
bool failReceive = false;
bool failResponse = false;
bool failHeader = false;
bool failPartialReceive = false;
int64_t nowUs = 1000;
int currentSocket = 7;
int sends = 0;
int shutdowns = 0;
httpd_ws_type_t lastSentType = HTTPD_WS_TYPE_TEXT;
std::map<int, httpd_req_t*> sessions;
std::deque<std::pair<httpd_work_fn_t, void*>> work;

class Trace final : public ShowTraceReadPort {
 public:
  std::string ReadTrace(uint64_t) override { ++reads; return json; }
  int reads = 0;
  std::string json = "{\"schemaVersion\":1}";
};

void DrainWork() {
  while (!work.empty()) {
    auto item = work.front();
    work.pop_front();
    item.first(item.second);
  }
}

void Drop(httpd_req_t& request) {
  if (request.sess_ctx != nullptr) {
    request.free_ctx(request.sess_ctx);
    request.sess_ctx = nullptr;
  }
  for (auto iterator = sessions.begin(); iterator != sessions.end();) {
    if (iterator->second == &request) {
      iterator = sessions.erase(iterator);
    } else {
      ++iterator;
    }
  }
}

class Storage final : public ProvisioningStorage, public LocalPairingStorage {
 public:
  ProvisioningReadResult Read(uint8_t*, size_t, size_t&) override {
    return ProvisioningReadResult::Missing;
  }
  bool Write(const uint8_t*, size_t) override { return !failWrite; }
  bool failWrite = false;
};

class Hasher final : public Sha256Port {
 public:
  bool Hash(const uint8_t* input, size_t length,
            std::array<uint8_t, 32>& digest) override {
    digest.fill(0x73);
    for (size_t index = 0; index < length; ++index) {
      digest[index % digest.size()] ^= input[index];
    }
    return true;
  }
};

class Random final : public PairingRandomPort {
 public:
  bool Fill(uint8_t* output, size_t length) override {
    if (fail) {
      return false;
    }
    std::memset(output, 0x1a, length);
    return true;
  }
  bool fail = false;
};

class Validator final : public LocalTlsValidator {
 public:
  bool Validate(const uint8_t*, size_t, const uint8_t*, size_t,
                std::array<uint8_t, 32>& digest) override {
    digest.fill(0x73);
    return true;
  }
};

class Gate final : public ServiceGate {
 public:
  bool ServiceModeActive() const override { return true; }
  bool ActuatorSupplyDisconnected() const override { return true; }
};

std::string Read(const char* path) {
  std::ifstream file(path, std::ios::binary);
  assert(file);
  return {std::istreambuf_iterator<char>(file),
          std::istreambuf_iterator<char>()};
}

httpd_req_t Request() {
  httpd_req_t request;
  request.handle = reinterpret_cast<void*>(1);
  request.uri = endpoint.uri;
  request.user_ctx = endpoint.user_ctx;
  return request;
}

void Connect(httpd_req_t& request) {
  assert(endpoint.ws_pre_handshake_cb(&request) == ESP_OK);
  assert(endpoint.ws_post_handshake_cb(&request) == ESP_OK);
  sessions[currentSocket] = &request;
}

}  // namespace

esp_err_t httpd_ssl_start(httpd_handle_t* handle,
                          const httpd_ssl_config_t* config) {
  configuration = *config;
  if (failStart) {
    return ESP_FAIL;
  }
  *handle = reinterpret_cast<void*>(1);
  return ESP_OK;
}
esp_err_t httpd_ssl_stop(httpd_handle_t) {
  DrainWork();
  while (!sessions.empty()) {
    Drop(*sessions.begin()->second);
  }
  return ESP_OK;
}
esp_err_t httpd_register_uri_handler(httpd_handle_t, const httpd_uri_t* uri) {
  if (std::strcmp(uri->uri, "/v1/show-trace") == 0) {
    traceEndpoint = *uri;
  } else if (uri->method == HTTP_GET) {
    endpoint = *uri;
  } else {
    pairingEndpoint = *uri;
  }
  return failRegister ? ESP_FAIL : ESP_OK;
}
size_t httpd_req_get_hdr_value_len(httpd_req_t*, const char* name) {
  if (std::strcmp(name, "Authorization") == 0) {
    return authorization.size();
  }
  if (std::strcmp(name, "Content-Type") == 0) {
    return contentType.size();
  }
  if (std::strcmp(name, "Transfer-Encoding") == 0) {
    return transferEncoding.size();
  }
  return extensions.size();
}
esp_err_t httpd_req_get_hdr_value_str(httpd_req_t*, const char* name, char* output,
                                      size_t capacity) {
  const auto& value = std::strcmp(name, "Content-Type") == 0
                          ? contentType : authorization;
  assert(capacity > value.size());
  std::memcpy(output, value.c_str(), value.size() + 1);
  return ESP_OK;
}
int httpd_req_to_sockfd(httpd_req_t*) { return currentSocket; }
esp_err_t httpd_resp_set_status(httpd_req_t*, const char* value) {
  status = value;
  return ESP_OK;
}
esp_err_t httpd_resp_send(httpd_req_t*, const char* bytes, int length) {
  responseBody.assign(bytes, static_cast<size_t>(length));
  return failResponse ? ESP_FAIL : ESP_OK;
}
esp_err_t httpd_resp_set_hdr(httpd_req_t*, const char* name, const char* value) {
  assert(std::strcmp(name, "Cache-Control") == 0 && std::strcmp(value, "no-store") == 0);
  return failHeader ? ESP_FAIL : ESP_OK;
}
esp_err_t httpd_resp_set_type(httpd_req_t*, const char* value) {
  assert(std::strcmp(value, "application/json") == 0);
  return ESP_OK;
}
int httpd_req_recv(httpd_req_t*, char* bytes, size_t maximum) {
  if (failReceive || (failPartialReceive && readOffset >= 6) ||
      readOffset == incoming.size()) {
    return -1;
  }
  const size_t remaining = incoming.size() - readOffset;
  const size_t count = remaining < maximum ? remaining : maximum;
  const size_t fragment = count < 3 ? count : 3;
  std::memcpy(bytes, incoming.data() + readOffset, fragment);
  readOffset += fragment;
  return static_cast<int>(fragment);
}
esp_err_t httpd_ws_recv_frame(httpd_req_t*, httpd_ws_frame_t* frame,
                              size_t maximum) {
  if (failReceive) {
    return ESP_FAIL;
  }
  if (maximum == 0) {
    frame->type = incomingType;
    frame->final = finalFrame;
    frame->len = incoming.size();
  } else {
    assert(maximum >= incoming.size());
    std::memcpy(frame->payload, incoming.data(), incoming.size());
  }
  return ESP_OK;
}
esp_err_t httpd_ws_send_frame_async(httpd_handle_t, int,
                                    httpd_ws_frame_t* frame) {
  ++sends;
  lastSentType = frame->type;
  return failSend ? ESP_FAIL : ESP_OK;
}
esp_err_t httpd_ws_send_frame(httpd_req_t* request, httpd_ws_frame_t* frame) {
  return httpd_ws_send_frame_async(request->handle, currentSocket, frame);
}
esp_err_t httpd_queue_work(httpd_handle_t, httpd_work_fn_t callback,
                            void* context) {
  if (failQueue) {
    return ESP_FAIL;
  }
  work.emplace_back(callback, context);
  return ESP_OK;
}
void* httpd_sess_get_ctx(httpd_handle_t, int socket) {
  const auto found = sessions.find(socket);
  return found == sessions.end() ? nullptr : found->second->sess_ctx;
}
int64_t esp_timer_get_time() { return nowUs; }
int shutdown(int socket, int mode) {
  assert(socket == 7 && mode == 2);
  ++shutdowns;
  return 0;
}

int main(int argc, char** argv) {
  assert(argc == 3);
  const std::string hello = Read(argv[1]);
  const std::string stop = Read(argv[2]);
  Storage storage;
  Hasher hasher;
  Random random;
  Validator validator;
  Gate gate;
  LocalTlsIdentity identity(storage, validator);
  LocalPairingCredential credential(storage, hasher, random);
  LocalBearerAuthenticator authenticator(credential);
  LocalSocketAdmission admission(authenticator);
  LocalControlInbox inbox("kufr-001", "c85743d5-1b72-41b8-8500-ae3c102b27d6");
  EspIdfLocalControlServer server(identity, credential, admission, inbox);
  assert(!server.Start());
  const char certificate[] = "-----BEGIN CERTIFICATE-----\nTEST\n-----END CERTIFICATE-----\n";
  const char key[] = "-----BEGIN PRIVATE KEY-----\nTEST\n-----END PRIVATE KEY-----\n";
  assert(identity.Provision(reinterpret_cast<const uint8_t*>(certificate),
                            sizeof(certificate),
                            reinterpret_cast<const uint8_t*>(key), sizeof(key),
                            gate));
  // A healthy missing pairing record permits discovery/enrollment while TLS
  // remains configured. It cannot authenticate a bearer until BLE pairs it.
  assert(server.Start());
  assert(!credential.IsPaired() && !credential.IsPairingReady());
  auto unpaired = Request();
  assert(endpoint.ws_pre_handshake_cb(&unpaired) == ESP_FAIL);
  server.Stop();
  std::array<uint8_t, 16> secret{};
  secret.fill(0x73);
  assert(credential.ProvisionPairingSecret(secret.data(), secret.size(), gate));
  authorization = "Bearer " + std::string(64, '0');
  for (size_t index = 7; index < authorization.size(); index += 2) {
    authorization[index] = '1';
    authorization[index + 1] = 'a';
  }
  failStart = true;
  assert(!server.Start());
  failStart = false;
  failRegister = true;
  assert(!server.Start());
  failRegister = false;
  assert(server.Start());
  assert(!server.Start());
  assert(configuration.transport_mode == HTTPD_SSL_TRANSPORT_SECURE);
  assert(configuration.port_secure == 443);
  assert(configuration.servercert == identity.Certificate());
  assert(configuration.prvtkey_pem == identity.PrivateKey());
  assert(!configuration.httpd.lru_purge_enable);
  assert(configuration.httpd.max_open_sockets == 2);
  assert(configuration.httpd.max_uri_handlers == 3);
  assert(endpoint.is_websocket && endpoint.handle_ws_control_frames);
  auto pairRequest = Request();
  pairRequest.uri = pairingEndpoint.uri;
  pairRequest.user_ctx = pairingEndpoint.user_ctx;
  const std::string validBody =
      "{\"protocolVersion\":1,\"pairingSecret\":\"" + std::string(32, '7') + "\"}";
  std::string actualBody = validBody;
  for (size_t index = actualBody.find(':', actualBody.find("pairingSecret")) + 2;
       index + 1 < actualBody.size() - 2; index += 2) {
    actualBody[index + 1] = '3';
  }
  auto callPair = [&](const std::string& body) {
    incoming = body;
    readOffset = 0;
    pairRequest.content_len = body.size();
    return pairingEndpoint.handler(&pairRequest);
  };
  assert(callPair(validBody) == ESP_FAIL && status == "401 Unauthorized");
  assert(credential.IsPairingReady());
  for (const std::string& body : {
           std::string("{}"), actualBody + "x",
           std::string("{\"protocolVersion\":1.0,\"pairingSecret\":\"00\"}"),
           std::string("{\"protocolVersion\":1,\"protocolVersion\":1}"),
           std::string("{\"protocolVersion\":1,\"unknown\":0}"),
           std::string("{\"protocolVersion\":1,\"pairingSecret\":\"GG\"}")}) {
    assert(callPair(body) == ESP_FAIL && status == "400 Bad Request");
  }
  assert(callPair(std::string(193, 'x')) == ESP_FAIL && status == "413 Content Too Large");
  contentType = "text/plain";
  assert(callPair(actualBody) == ESP_FAIL && status == "415 Unsupported Media Type");
  contentType = "application/json";
  transferEncoding = "chunked";
  assert(callPair(actualBody) == ESP_FAIL && status == "400 Bad Request");
  transferEncoding.clear();
  pairRequest.uri = "/v1/pair?secret=forbidden";
  assert(callPair(actualBody) == ESP_FAIL && status == "400 Bad Request");
  pairRequest.uri = pairingEndpoint.uri;
  failReceive = true;
  assert(callPair(actualBody) == ESP_FAIL && credential.IsPairingReady());
  failReceive = false;
  failPartialReceive = true;
  assert(callPair(actualBody) == ESP_FAIL && readOffset == 6 && credential.IsPairingReady());
  failPartialReceive = false;
  failHeader = true;
  assert(callPair(actualBody) == ESP_FAIL && readOffset == 0 && credential.IsPairingReady());
  failHeader = false;
  assert(callPair(actualBody) == ESP_OK && credential.IsPaired());
  assert(responseBody == "{\"protocolVersion\":1,\"token\":\"" + authorization.substr(7) + "\"}");
  assert(callPair(actualBody) == ESP_FAIL && status == "409 Conflict");
  auto rejected = Request();
  const std::string validAuthorization = authorization;
  authorization.clear();
  assert(endpoint.ws_pre_handshake_cb(&rejected) == ESP_FAIL);
  assert(status == "401 Unauthorized" && rejected.sess_ctx == nullptr);
  authorization = validAuthorization;
  // Diagnostics authenticate independently of WebSocket/manual admission.
  Trace trace;
  server.BindShowTrace(trace);
  httpd_req_t traceRequest{};
  traceRequest.uri = traceEndpoint.uri;
  traceRequest.user_ctx = traceEndpoint.user_ctx;
  assert(!traceEndpoint.is_websocket);
  assert(traceEndpoint.handler(&traceRequest) == ESP_OK);
  assert(responseBody == trace.json && trace.reads == 1);
  authorization.clear();
  assert(traceEndpoint.handler(&traceRequest) == ESP_FAIL);
  assert(status == "401 Unauthorized" && trace.reads == 1);
  authorization = validAuthorization;
  traceRequest.uri = "/v1/show-trace?token=forbidden";
  assert(traceEndpoint.handler(&traceRequest) == ESP_FAIL);
  assert(status == "400 Bad Request" && trace.reads == 1);
  traceRequest.uri = traceEndpoint.uri;
  traceRequest.content_len = 1;
  assert(traceEndpoint.handler(&traceRequest) == ESP_FAIL && trace.reads == 1);
  traceRequest.content_len = 0;
  transferEncoding = "chunked";
  assert(traceEndpoint.handler(&traceRequest) == ESP_FAIL && trace.reads == 1);
  transferEncoding.clear();
  trace.json.clear();
  assert(traceEndpoint.handler(&traceRequest) == ESP_FAIL && status == "503 Service Unavailable");
  trace.json.assign(65537, 'x');
  assert(traceEndpoint.handler(&traceRequest) == ESP_FAIL && status == "503 Service Unavailable");
  trace.json = "{\"schemaVersion\":1}";
  failHeader = true;
  assert(traceEndpoint.handler(&traceRequest) == ESP_FAIL);
  failHeader = false;
  rejected.uri = "/v1/control?token=forbidden";
  assert(endpoint.ws_pre_handshake_cb(&rejected) == ESP_FAIL);
  rejected.uri = endpoint.uri;
  extensions = "permessage-deflate; client_max_window_bits";
  assert(endpoint.ws_pre_handshake_cb(&rejected) == ESP_OK);
  Drop(rejected);
  // Declining compression must not bypass the bearer admission gate.
  authorization[7] = 'z';
  auto unauthorizedOffer = Request();
  assert(endpoint.ws_pre_handshake_cb(&unauthorizedOffer) == ESP_FAIL);
  assert(status == "401 Unauthorized" && unauthorizedOffer.sess_ctx == nullptr);
  authorization = validAuthorization;
  extensions.clear();
  auto expired = Request();
  assert(endpoint.ws_pre_handshake_cb(&expired) == ESP_OK);
  nowUs += 10000000;
  assert(endpoint.ws_post_handshake_cb(&expired) == ESP_FAIL);
  Drop(expired);
  auto active = Request();
  Connect(active);
  auto busy = Request();
  currentSocket = 8;
  assert(endpoint.ws_pre_handshake_cb(&busy) == ESP_FAIL);
  assert(status == "409 Conflict");
  authorization[7] = 'f';
  assert(endpoint.ws_pre_handshake_cb(&busy) == ESP_FAIL);
  assert(status == "401 Unauthorized");
  authorization = validAuthorization;
  currentSocket = 7;
  LocalControlEvent event;
  assert(inbox.Take(event) && event.kind == LocalControlEventKind::Connected);
  const auto first = event.handle;
  assert(admission.IsActive(first));
  incoming = hello;
  assert(endpoint.handler(&active) == ESP_OK);
  assert(inbox.Take(event) && event.message.kind == LocalControlMessageKind::Hello);
  finalFrame = false;
  assert(endpoint.handler(&active) == ESP_FAIL);
  finalFrame = true;
  incomingType = HTTPD_WS_TYPE_BINARY;
  assert(endpoint.handler(&active) == ESP_FAIL);
  incomingType = HTTPD_WS_TYPE_CONTINUE;
  assert(endpoint.handler(&active) == ESP_FAIL);
  incomingType = HTTPD_WS_TYPE_TEXT;
  incoming.assign(8193, 'x');
  assert(endpoint.handler(&active) == ESP_FAIL);
  incoming = "{}";
  assert(endpoint.handler(&active) == ESP_FAIL);
  incoming = hello;
  failReceive = true;
  assert(endpoint.handler(&active) == ESP_FAIL);
  failReceive = false;
  incoming = "ping";
  incomingType = HTTPD_WS_TYPE_PING;
  assert(endpoint.handler(&active) == ESP_OK);
  assert(lastSentType == HTTPD_WS_TYPE_PONG);
  incoming.assign(126, 'x');
  assert(endpoint.handler(&active) == ESP_FAIL);
  incoming.clear();
  assert(endpoint.handler(&active) == ESP_OK);
  incomingType = HTTPD_WS_TYPE_PONG;
  assert(endpoint.handler(&active) == ESP_OK);
  incomingType = HTTPD_WS_TYPE_TEXT;
  incoming = hello;
  for (size_t index = 0; index < LocalControlInbox::QueueCapacity; ++index) {
    assert(endpoint.handler(&active) == ESP_OK);
  }
  assert(endpoint.handler(&active) == ESP_FAIL);
  incoming = stop;
  assert(endpoint.handler(&active) == ESP_OK);
  assert(inbox.Take(event) && event.kind == LocalControlEventKind::Stop);
  assert(!inbox.Take(event));
  const int beforeSend = sends;
  assert(server.Send(first, "{}"));
  assert(work.size() == 1);
  DrainWork();
  assert(sends == beforeSend + 1);
  failQueue = true;
  for (size_t index = 0; index < EspIdfLocalControlServer::OutputCapacity; ++index) {
    assert(server.Send(first, "{}"));
  }
  assert(!server.Send(first, "{}"));
  assert(inbox.Take(event) && event.kind == LocalControlEventKind::Disconnected);
  assert(work.empty());
  // Release/reuse the same FD before the deferred close worker can run.
  Drop(active);
  auto reused = Request();
  Connect(reused);
  assert(inbox.Take(event) && event.kind == LocalControlEventKind::Connected);
  const auto second = event.handle;
  assert(second.generation != first.generation);
  failQueue = false;
  server.Pump();
  DrainWork();
  assert(shutdowns == 0);
  assert(!server.Send(first, "{}"));
  failSend = true;
  assert(server.Send(second, "{}"));
  DrainWork();
  assert(shutdowns == 1);
  assert(inbox.Take(event) && event.kind == LocalControlEventKind::Disconnected);
  failSend = false;
  Drop(reused);
  auto closing = Request();
  Connect(closing);
  assert(inbox.Take(event));
  incoming.clear();
  incomingType = HTTPD_WS_TYPE_CLOSE;
  assert(endpoint.handler(&closing) == ESP_FAIL);
  assert(inbox.Take(event) && event.kind == LocalControlEventKind::Disconnected);
  Drop(closing);
  server.Stop();
  assert(server.Start());
  server.Stop();

  assert(credential.ProvisionPairingSecret(secret.data(), secret.size(), gate));
  assert(server.Start());
  random.fail = true;
  assert(callPair(actualBody) == ESP_FAIL && credential.IsPairingReady());
  random.fail = false;
  failResponse = true;
  assert(callPair(actualBody) == ESP_FAIL && credential.IsPaired());
  failResponse = false;
  assert(callPair(actualBody) == ESP_FAIL && status == "409 Conflict");
  server.Stop();
  assert(credential.ProvisionPairingSecret(secret.data(), secret.size(), gate));
  assert(server.Start());
  storage.failWrite = true;
  assert(callPair(actualBody) == ESP_FAIL && status == "503 Service Unavailable");
  assert(!credential.IsHealthy() && !credential.IsPaired());
  assert(callPair(actualBody) == ESP_FAIL && status == "503 Service Unavailable");
  server.Stop();
}

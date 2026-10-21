#include "../src/features/voice_turn/EspIdfDeviceMediaSocket.h"
#include "esp_crt_bundle.h"
#include "esp_tls_errors.h"
#include <cstdarg>
#include <fcntl.h>
#include <algorithm>
#include <array>
#include <cstring>
#include <deque>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>

namespace {
using Result = DeviceMediaSocketPort::ReceiveResult;
constexpr const char* Id = "12345678-1234-1234-1234-123456789abc";
constexpr const char* Host = "device.example.invalid";
constexpr const char* Token = "c3ludGhldGljLXRlc3QtdG9rZW4=";
const std::string ValidHeaders =
    "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
    "Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n"
    "Sec-WebSocket-Protocol: kufr-device-media-v1\r\n\r\n";
void Check(bool condition, const char* message) { if (!condition) { throw std::runtime_error(message); } }
struct Fixture {
  std::deque<uint8_t> incoming;
  std::vector<std::vector<uint8_t>> writes;
  std::string response = ValidHeaders;
  std::string connectedHost;
  size_t readLimit = 2048;
  int connectResult = 0, writeShort = 0, readError = 0;
  int reads = 0, created = 0, destroyed = 0, closed = 0;
  int64_t clock = 0, connectDelay = 0, readDelay = 1;
  bool bundle = false, cryptoFailure = false, nonblocking = false, nonblockingFailure = false;
  const uint8_t* lastWrite = nullptr;
  size_t lastWriteSize = 0;
  void Feed(const std::vector<uint8_t>& bytes) { incoming.insert(incoming.end(), bytes.begin(), bytes.end()); }
} *fixture;
struct Scope {
  Fixture value;
  Scope() { fixture = &value; }
  ~Scope() { fixture = nullptr; }
};
void Open(EspIdfDeviceMediaSocket& socket) { Check(socket.Open(Host, Id, Token, Id), socket.Error()); }
std::vector<uint8_t> Frame(uint8_t opcode, bool fin, const std::vector<uint8_t>& payload) {
  std::vector<uint8_t> bytes{static_cast<uint8_t>((fin ? 128 : 0) | opcode)};
  const size_t size = payload.size();
  if (size < 126) { bytes.push_back(static_cast<uint8_t>(size)); }
  else if (size < 65536) { bytes.push_back(126); bytes.push_back(static_cast<uint8_t>(size >> 8)); bytes.push_back(static_cast<uint8_t>(size)); }
  else {
    bytes.push_back(127);
    for (int shift = 56; shift >= 0; shift -= 8) { bytes.push_back(static_cast<uint8_t>(static_cast<uint64_t>(size) >> shift)); }
  }
  bytes.insert(bytes.end(), payload.begin(), payload.end()); return bytes;
}
std::vector<uint8_t> DecodeClientFrame(const std::vector<uint8_t>& bytes, uint8_t opcode) {
  Check(bytes.size() >= 6 && bytes[0] == (128 | opcode) && (bytes[1] & 128), "client frame must be FIN and masked");
  const size_t mask = (bytes[1] & 127) == 126 ? 4 : 2;
  const size_t size = mask == 4 ? (static_cast<size_t>(bytes[2]) << 8) | bytes[3] : bytes[1] & 127;
  Check(bytes.size() == mask + 4 + size, "client frame exact payload length");
  std::vector<uint8_t> plain(size);
  for (size_t i = 0; i < size; ++i) { plain[i] = bytes[mask + 4 + i] ^ bytes[mask + i % 4]; }
  return plain;
}
Result UntilMessage(EspIdfDeviceMediaSocket& socket, uint8_t* workspace, size_t cap, size_t& used, bool& binary) {
  for (int i = 0; i < 70000; ++i) {
    const int before = fixture->reads;
    const Result result = socket.Receive(workspace, cap, used, binary);
    Check(fixture->reads - before <= 1, "Receive must make at most one TLS read");
    if (result != Result::Idle) { return result; }
    Check(used == 0, "partial message cannot escape as complete");
  }
  throw std::runtime_error("bounded test receive exhausted");
}
void Handshake() {
  Scope scope; EspIdfDeviceMediaSocket socket; Open(socket);
  Check(scope.value.bundle && scope.value.connectedHost == Host, "TLS bundle and original hostname required");
  const auto& bytes = scope.value.writes.front();
  const std::string request(bytes.begin(), bytes.end());
  Check(request.find(std::string("GET /v1/conversations/") + Id + "/device-media HTTP/1.1\r\n") == 0, "exact upgrade route");
  Check(request.find(std::string("Authorization: Bearer ") + Token + "\r\n") != std::string::npos, "bearer header");
  Check(request.find(std::string("Idempotency-Key: ") + Id + "\r\n") != std::string::npos, "original opening key");
  Check(request.find("Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n") != std::string::npos, "RFC6455 nonce vector");
  Check(std::all_of(scope.value.lastWrite, scope.value.lastWrite + scope.value.lastWriteSize,
                    [](uint8_t b) { return b == 0; }), "owned upgrade credential buffer scrubbed");
  socket.Close(); Check(scope.value.destroyed == 1 && scope.value.closed == 1, "single SSL owner cleanup");
  socket.Close(); Check(scope.value.destroyed == 1, "idempotent cleanup");
}
void InvalidOpening() {
  const std::vector<std::string> hosts{"", "https://device.example.invalid", "device.example.invalid/path", "user@device.example.invalid",
      "device.example.invalid:443", "device.example.invalid\r\nX: y", ".host", "host.", "-host", "host-", std::string(64, 'a') + ".invalid"};
  for (const auto& host : hosts) {
    Scope scope; EspIdfDeviceMediaSocket socket;
    Check(!socket.Open(host, Id, Token, Id) && scope.value.created == 0, "invalid host rejected before TLS");
  }
  for (const auto& token : {std::string(""), std::string("bad\r\nheader"), std::string(257, 'a'), std::string("has space")}) {
    Scope scope; EspIdfDeviceMediaSocket socket;
    Check(!socket.Open(Host, Id, token, Id) && scope.value.created == 0, "invalid bearer rejected before TLS");
  }
  for (const auto& id : {std::string("bad"), std::string("12345678-1234-1234-1234-123456789ABC")}) {
    Scope scope; EspIdfDeviceMediaSocket socket;
    Check(!socket.Open(Host, id, Token, Id) && !socket.Open(Host, Id, Token, id) && scope.value.created == 0, "UUID canonical before TLS");
  }
}
void HandshakeRefusals() {
  const std::vector<std::pair<std::string, std::string>> changes{
      {"HTTP/1.1 101", "HTTP/1.1 302"}, {"HTTP/1.1 101", "HTTP/1.1 1010"},
      {"Sec-WebSocket-Protocol: kufr-device-media-v1\r\n", ""},
      {"Sec-WebSocket-Protocol: kufr-device-media-v1", "Sec-WebSocket-Protocol: other"},
      {"Sec-WebSocket-Protocol: kufr-device-media-v1", "Sec-WebSocket-Protocol: kufr-device-media-v1, other"},
      {"Sec-WebSocket-Protocol: kufr-device-media-v1", "Sec-WebSocket-Protocol: kufr-device-media-v1\r\nsec-websocket-protocol: kufr-device-media-v1"},
      {"s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", "wrong"},
      {"Upgrade: websocket", "Upgrade: websocket\r\nUpgrade: websocket"},
      {"Connection: Upgrade", "Connection: close"},
      {"Connection: Upgrade", "Connection: Upgrade\r\nConnection: Upgrade"},
      {"Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", "Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\nSec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo="},
      {"Upgrade: websocket", "Upgrade: websocket\r\nSec-WebSocket-Extensions: permessage-deflate"},
      {"Upgrade: websocket", " Upgrade: websocket"},
      {"Connection: Upgrade", "Connection: Upgrade, Upgrade"},
      {"Upgrade: websocket", "Upgrade: websocket\nBad: header"},
      {"Upgrade: websocket", "Upgrade : websocket"},
      {"Upgrade: websocket", std::string("Upgrade: websocket\r\nX: ") + char(0) + "value"},
  };
  for (const auto& change : changes) {
    Scope scope; EspIdfDeviceMediaSocket socket;
    auto& response = scope.value.response; response.replace(response.find(change.first), change.first.size(), change.second);
    Check(!socket.Open(Host, Id, Token, Id), "invalid handshake accepted");
    Check(scope.value.created == 1 && scope.value.destroyed == 1, "failed handshake destroys original handle without redirect");
  }
  { Scope scope; EspIdfDeviceMediaSocket socket; scope.value.nonblockingFailure = true;
    Check(!socket.Open(Host, Id, Token, Id) && scope.value.writes.empty(), "nonblocking failure before bearer transmission"); }
  { Scope scope; EspIdfDeviceMediaSocket socket;
    const auto at = scope.value.response.find("Connection: Upgrade");
    scope.value.response.replace(at, 19, "Connection: keep-alive, uPgRaDe"); Open(socket); }
  { Scope scope; EspIdfDeviceMediaSocket socket; scope.value.response = std::string(4096, 'x');
    Check(!socket.Open(Host, Id, Token, Id) && scope.value.reads == 4096, "header cap without unbounded read"); }
  { Scope scope; EspIdfDeviceMediaSocket socket; scope.value.response.clear();
    Check(!socket.Open(Host, Id, Token, Id) && scope.value.clock <= 5000000, "absolute idle handshake deadline"); }
  { Scope scope; EspIdfDeviceMediaSocket socket; scope.value.readDelay = 100000;
    Check(!socket.Open(Host, Id, Token, Id) && scope.value.reads <= 50, "absolute trickle handshake deadline"); }
  { Scope scope; EspIdfDeviceMediaSocket socket; scope.value.connectDelay = 5000000;
    Check(!socket.Open(Host, Id, Token, Id) && scope.value.writes.empty(), "connect exhaustion cannot send bearer"); }
  { Scope scope; EspIdfDeviceMediaSocket socket; scope.value.cryptoFailure = true;
    Check(!socket.Open(Host, Id, Token, Id) && scope.value.writes.empty(), "crypto failure closes before auth request"); }
  { Scope scope; EspIdfDeviceMediaSocket socket; scope.value.writeShort = 1;
    Check(!socket.Open(Host, Id, Token, Id) && scope.value.writes.size() == 1, "ambiguous opening write is never replayed");
    Check(std::all_of(scope.value.lastWrite, scope.value.lastWrite + scope.value.lastWriteSize,
                     [](uint8_t b) { return b == 0; }), "failed opening scratch scrubbed"); }
}
void SendBoundsAndUnknown() {
  Scope scope; EspIdfDeviceMediaSocket socket; Open(socket);
  const std::vector<uint8_t> original(4112, 0xa7); const auto copy = original;
  Check(socket.Send(true, original.data(), original.size()), "bounded binary send");
  Check(DecodeClientFrame(scope.value.writes.back(), 2) == original && original == copy, "mask owns copy, caller immutable");
  const uint8_t json[] = {'{', '}'};
  Check(socket.Send(false, json, sizeof(json)) && DecodeClientFrame(scope.value.writes.back(), 1) == std::vector<uint8_t>(json, json + 2), "text frame");
  const std::vector<uint8_t> maximum(8184, 42);
  Check(socket.Send(true, maximum.data(), maximum.size()) && scope.value.writes.back().size() == 8192 &&
        DecodeClientFrame(scope.value.writes.back(), 2) == maximum, "exact full transmit capacity");
  scope.value.writeShort = 1;
  Check(!socket.Send(true, original.data(), original.size()) && original == copy, "ambiguous write never mutates caller");
  Check(std::all_of(scope.value.lastWrite, scope.value.lastWrite + scope.value.lastWriteSize,
                    [](uint8_t b) { return b == 0; }), "masked scratch scrubbed on short write");
  const size_t writes = scope.value.writes.size();
  Check(!socket.Send(true, original.data(), original.size()) && scope.value.writes.size() == writes, "unknown send poisons, no replay");
  scope.value.writeShort = 0; Open(socket); const std::vector<uint8_t> tooLarge(8185, 1);
  Check(!socket.Send(true, tooLarge.data(), tooLarge.size()), "send allocation cap");
  Open(socket); const uint8_t invalidText[] = {0xff};
  Check(!socket.Send(false, invalidText, sizeof(invalidText)), "outgoing text must be valid UTF8");
}
void FragmentationAndControl() {
  Scope scope; EspIdfDeviceMediaSocket socket; Open(socket); scope.value.readLimit = 1;
  scope.value.Feed(Frame(2, false, {1, 2})); scope.value.Feed(Frame(9, true, {'h', 'i'}));
  scope.value.Feed(Frame(10, true, {})); scope.value.Feed(Frame(0, true, {3, 4}));
  std::array<uint8_t, 65536> workspace{}; size_t used = 99; bool binary = false;
  Check(UntilMessage(socket, workspace.data(), workspace.size(), used, binary) == Result::Message && used == 4 && binary, "fragmented binary complete only after continuation FIN");
  Check(std::equal(workspace.begin(), workspace.begin() + 4, std::vector<uint8_t>{1, 2, 3, 4}.begin()), "interleaved control cannot contaminate data");
  Check(scope.value.writes.size() == 2 && DecodeClientFrame(scope.value.writes.back(), 10) == std::vector<uint8_t>({'h', 'i'}), "sole actor masked exact PONG");
  scope.value.Feed(Frame(1, false, {0xe2})); scope.value.Feed(Frame(0, true, {0x82, 0xac}));
  Check(UntilMessage(socket, workspace.data(), workspace.size(), used, binary) == Result::Message && used == 3 && !binary, "UTF8 spans fragments");
  const int before = scope.value.reads;
  Check(socket.Receive(workspace.data(), workspace.size(), used, binary) == Result::Idle && used == 0 && scope.value.reads == before + 1, "idle single polling read");
  scope.value.Feed(Frame(8, true, {3, 232}));
  Check(UntilMessage(socket, workspace.data(), workspace.size(), used, binary) == Result::Closed, "valid close terminates actor");
  Check(DecodeClientFrame(scope.value.writes.back(), 8) == std::vector<uint8_t>({3, 232}), "close echoed once");
}
void ReceiveBounds() {
  for (bool binaryMessage : {false, true}) {
    Scope scope; EspIdfDeviceMediaSocket socket; Open(socket);
    const size_t expected = binaryMessage ? 65536 : 65535;
    scope.value.Feed(Frame(binaryMessage ? 2 : 1, true, std::vector<uint8_t>(expected, 'a')));
    std::array<uint8_t, 65536> workspace{}; size_t used = 0; bool binary = false;
    Check(UntilMessage(socket, workspace.data(), workspace.size(), used, binary) == Result::Message && used == expected && binary == binaryMessage, "exact total receive bounds");
  }
  { Scope scope; EspIdfDeviceMediaSocket socket; Open(socket); std::array<uint8_t, 8> workspace{};
    scope.value.Feed(Frame(2, true, std::vector<uint8_t>(9, 1))); size_t used; bool binary;
    Check(socket.Receive(workspace.data(), workspace.size(), used, binary) == Result::Failed, "caller workspace cap checked before copy"); }
  { Scope scope; EspIdfDeviceMediaSocket socket; Open(socket); scope.value.Feed(Frame(2, false, {1}));
    std::array<uint8_t, 8> first{}, second{}; size_t used; bool binary;
    Check(socket.Receive(first.data(), first.size(), used, binary) == Result::Idle, "partial owns original workspace");
    Check(socket.Receive(second.data(), second.size(), used, binary) == Result::Failed, "workspace substitution fails closed"); }
  { Scope scope; EspIdfDeviceMediaSocket socket; Open(socket);
    scope.value.Feed(Frame(2, false, std::vector<uint8_t>(65535, 1)));
    scope.value.Feed(Frame(0, true, {1, 2}));
    std::array<uint8_t, 65536> workspace{}; size_t used; bool binary;
    Check(UntilMessage(socket, workspace.data(), workspace.size(), used, binary) == Result::Failed, "aggregate fragmented size cap"); }
  { Scope scope; EspIdfDeviceMediaSocket socket; Open(socket);
    scope.value.Feed(Frame(9, true, {1})); scope.value.Feed(Frame(9, true, {2})); scope.value.Feed(Frame(2, true, {3}));
    std::array<uint8_t, 8> workspace{}; size_t used; bool binary;
    const size_t before = scope.value.writes.size();
    Check(socket.Receive(workspace.data(), workspace.size(), used, binary) == Result::Idle && scope.value.writes.size() == before + 1, "one PONG per actor visit");
    Check(socket.Receive(workspace.data(), workspace.size(), used, binary) == Result::Idle && scope.value.writes.size() == before + 2, "buffered next PING yields separately");
    Check(socket.Receive(workspace.data(), workspace.size(), used, binary) == Result::Message && used == 1, "data after control flood remains available"); }
}
void RetryableReadProgress() {
  for (int retryable : {0, ESP_TLS_ERR_SSL_WANT_READ, ESP_TLS_ERR_SSL_WANT_WRITE, ESP_TLS_ERR_SSL_TIMEOUT}) {
    Scope scope; EspIdfDeviceMediaSocket socket; Open(socket); scope.value.readLimit = 1;
    scope.value.Feed(Frame(2, true, {10, 20}));
    std::array<uint8_t, 8> workspace{}; size_t used; bool binary;
    Check(socket.Receive(workspace.data(), workspace.size(), used, binary) == Result::Idle, "partial header retained");
    scope.value.readError = retryable;
    if (!retryable) { scope.value.incoming.clear(); }
    Check(socket.Receive(workspace.data(), workspace.size(), used, binary) == Result::Idle && scope.value.destroyed == 0, "known TLS read progress yields without poisoning");
    if (!retryable) { scope.value.Feed({2, 10, 20}); }
    scope.value.readError = 0;
    Check(UntilMessage(socket, workspace.data(), workspace.size(), used, binary) == Result::Message && used == 2 && workspace[0] == 10 && workspace[1] == 20, "original message resumes after known read progress");
  }
}
void Malformed() {
  const std::vector<std::vector<uint8_t>> frames{
      {0x82, 0x80}, {0xc2, 0}, {0x83, 0}, {0x80, 0}, {0x09, 0}, {0x89, 126, 0, 126},
      {0x82, 126, 0, 1}, {0x82, 127, 0, 0, 0, 0, 0, 0, 0, 126},
      {0x82, 127, 0x80, 0, 0, 0, 0, 1, 0, 0},
      {0x82, 127, 0, 0, 0, 0, 0, 1, 0, 1}, // 65537: refuse without draining
      {0x81, 127, 0, 0, 0, 0, 0, 1, 0, 0}, // text limit65535
      {0x81, 1, 0xff}, {0x81, 1, 0xc2}, {0x81, 3, 0xed, 0xa0, 0x80},
      {0x88, 1, 0}, {0x88, 2, 3, 237}, {0x88, 3, 3, 232, 0xff},
      {0x02, 1, 1, 0x82, 1, 2}, // new data before continuation
  };
  for (const auto& frame : frames) {
    Scope scope; EspIdfDeviceMediaSocket socket; Open(socket); scope.value.Feed(frame);
    std::array<uint8_t, 65536> workspace{}; size_t used; bool binary;
    Check(UntilMessage(socket, workspace.data(), workspace.size(), used, binary) == Result::Failed, "malformed frame accepted");
    Check(scope.value.destroyed == 1, "malformed closes handle");
  }
  for (int failure : {-1, -2, -12345}) {
    Scope scope; EspIdfDeviceMediaSocket socket; Open(socket); scope.value.readError = failure;
    std::array<uint8_t, 8> workspace{}; size_t used; bool binary;
    Check(socket.Receive(workspace.data(), workspace.size(), used, binary) == Result::Failed,
          "raw EOF without WebSocket close and unknown TLS errors are terminal failures");
  }
}
}

struct esp_transport_item_t {};
extern "C" {
esp_transport_handle_t esp_transport_ssl_init() { ++fixture->created; return new esp_transport_item_t; }
void esp_transport_ssl_crt_bundle_attach(esp_transport_handle_t, esp_err_t (*attach)(void*)) {
  Check(attach == esp_crt_bundle_attach, "use real bundle attachment API"); fixture->bundle = true;
}
int esp_crt_bundle_attach(void*) { return 0; }
int esp_transport_connect(esp_transport_handle_t, const char* host, int port, int timeout) {
  Check(fixture->bundle && port == 443 && timeout > 0 && timeout <= 5000, "verified TLS443 bounded connect");
  fixture->connectedHost = host; fixture->clock += fixture->connectDelay;
  fixture->incoming.insert(fixture->incoming.end(), fixture->response.begin(), fixture->response.end()); return fixture->connectResult;
}
int esp_transport_read(esp_transport_handle_t, char* buffer, int length, int timeout) {
  Check(fixture->nonblocking && length > 0 && length <= 2048 && timeout > 0 && timeout <= 50, "nonblocking bounded TLS read API");
  ++fixture->reads;
  if (fixture->readError) { return fixture->readError; }
  if (fixture->incoming.empty()) { fixture->clock += timeout * 1000; return 0; }
  fixture->clock += fixture->readDelay;
  const size_t count = std::min({static_cast<size_t>(length), fixture->readLimit, fixture->incoming.size()});
  for (size_t i = 0; i < count; ++i) { buffer[i] = static_cast<char>(fixture->incoming.front()); fixture->incoming.pop_front(); }
  return static_cast<int>(count);
}
int esp_transport_write(esp_transport_handle_t, const char* bytes, int length, int timeout) {
  Check(fixture->nonblocking && timeout > 0 && timeout <= 250 && length > 0 && length <= 8192, "one nonblocking bounded TLS write");
  fixture->lastWrite = reinterpret_cast<const uint8_t*>(bytes); fixture->lastWriteSize = static_cast<size_t>(length);
  fixture->writes.emplace_back(bytes, bytes + length);
  return fixture->writeShort ? length - fixture->writeShort : length;
}
int esp_transport_close(esp_transport_handle_t) { ++fixture->closed; return 0; }
int esp_transport_get_socket(esp_transport_handle_t) { return 42; }
int fcntl(int fd, int command, ...) {
  Check(fd == 42, "only own TLS descriptor configured");
  if (fixture->nonblockingFailure) { return -1; }
  if (command == F_GETFL) { return 0; }
  Check(command == F_SETFL, "nonblocking flag command");
  va_list args; va_start(args, command); const int flags = va_arg(args, int); va_end(args);
  Check((flags & O_NONBLOCK) != 0, "set O_NONBLOCK"); fixture->nonblocking = true; return 0;
}
esp_err_t esp_transport_destroy(esp_transport_handle_t handle) { ++fixture->destroyed; delete handle; return 0; }
int64_t esp_timer_get_time() { return fixture->clock; }
void esp_fill_random(void* output, size_t size) {
  if (size == 16) { std::memcpy(output, "the sample nonce", 16); }
  else { auto* bytes = static_cast<uint8_t*>(output); for (size_t i = 0; i < size; ++i) { bytes[i] = static_cast<uint8_t>(i + 1); } }
}
int esp_crypto_sha1(const unsigned char* input, size_t size, unsigned char output[20]) {
  if (fixture->cryptoFailure) { return -1; }
  // Stub the pinned crypto boundary with RFC6455 section1.3's exact vector.
  // The production digest implementation is ESP-TLS/mbedTLS, not this test.
  Check(std::string(reinterpret_cast<const char*>(input), size) == "dGhlIHNhbXBsZSBub25jZQ==258EAFA5-E914-47DA-95CA-C5AB0DC85B11", "exact SHA1 challenge");
  const uint8_t digest[20]{0xb3,0x7a,0x4f,0x2c,0xc0,0x62,0x4f,0x16,0x90,0xf6,0x46,0x06,0xcf,0x38,0x59,0x45,0xb2,0xbe,0xc4,0xea};
  std::memcpy(output, digest, 20); return 0;
}
int esp_crypto_base64_encode(unsigned char* output, size_t cap, size_t* used, const unsigned char* input, size_t size) {
  const char* alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
  *used = ((size + 2) / 3) * 4;
  if (cap <= *used) { return -1; }
  size_t at = 0;
  for (size_t i = 0; i < size; i += 3) {
    const uint32_t value = (static_cast<uint32_t>(input[i]) << 16) |
        (i + 1 < size ? static_cast<uint32_t>(input[i + 1]) << 8 : 0) | (i + 2 < size ? input[i + 2] : 0);
    output[at++] = alphabet[(value >> 18) & 63]; output[at++] = alphabet[(value >> 12) & 63];
    output[at++] = i + 1 < size ? alphabet[(value >> 6) & 63] : '=';
    output[at++] = i + 2 < size ? alphabet[value & 63] : '=';
  }
  output[at] = 0; return 0;
}
}
int main() {
  try {
    void (*checks[])() = {Handshake, InvalidOpening, HandshakeRefusals, SendBoundsAndUnknown,
                         FragmentationAndControl, ReceiveBounds, RetryableReadProgress, Malformed};
#if defined(CONFIG_MBEDTLS_DEBUG) && CONFIG_MBEDTLS_DEBUG
    (void)checks;
    Scope scope; EspIdfDeviceMediaSocket socket;
    Check(!socket.Open(Host, Id, Token, Id) && std::string(socket.Error()) == "tls_debug_unsupported" &&
          scope.value.created == 0 && scope.value.writes.empty(), "debug build refuses credentials before TLS setup");
    std::cout << "espidf_device_media_socket_test debug guard passed (no TLS/no bearer write)\n";
#else
    for (auto check : checks) { check(); }
    std::cout << "espidf_device_media_socket_test passed (fake TLS, RFC6455 crypto vector)\n";
#endif
    return 0;
  } catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}

#include "EspIdfDeviceMediaSocket.h"
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <fcntl.h>
#include "sdkconfig.h"
#include "esp_crt_bundle.h"
#include "esp_random.h"
#include "esp_timer.h"
#include "esp_tls_errors.h"
#include "esp_tls_crypto.h"
#include "esp_transport_ssl.h"

namespace {
constexpr size_t MaxMessage = 65536;
constexpr const char* Protocol = "kufr-device-media-v1";
void Wipe(void* bytes, size_t size) {
  volatile uint8_t* current = static_cast<volatile uint8_t*>(bytes);
  while (size--) { *current++ = 0; }
}
bool Uuid(const std::string& value) {
  if (value.size() != 36) { return false; }
  for (size_t i = 0; i < value.size(); ++i) {
    if (i == 8 || i == 13 || i == 18 || i == 23) {
      if (value[i] != '-') { return false; }
    } else if (!((value[i] >= '0' && value[i] <= '9') ||
                 (value[i] >= 'a' && value[i] <= 'f'))) { return false; }
  }
  return true;
}
bool Host(const std::string& value) {
  if (value.empty() || value.size() > 253) { return false; }
  size_t label = 0;
  for (size_t i = 0; i < value.size(); ++i) {
    const char c = value[i];
    if (c == '.') {
      if (!label || value[i - 1] == '-') { return false; }
      label = 0;
    } else {
      if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-') ||
          (!label && c == '-') || ++label > 63) { return false; }
    }
  }
  return label && value.back() != '-';
}
bool Token(const std::string& value) {
  return !value.empty() && value.size() <= 256 &&
      value.find_first_not_of("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=") == std::string::npos;
}
int Remaining(int64_t deadline, int maximum) {
  const int64_t left = deadline - esp_timer_get_time();
  return left <= 0 ? 0 : static_cast<int>(std::min<int64_t>(maximum, (left + 999) / 1000));
}
bool ReadIdle(int result) {
  // transport_ssl maps WANT_READ and TIMEOUT to zero in pinned IDF6.1, but
  // propagates WANT_WRITE. These are known read-progress states, not EOF or an
  // unknown send outcome; the next actor visit retains the original parser.
  return result == 0 || result == ESP_TLS_ERR_SSL_WANT_READ ||
      result == ESP_TLS_ERR_SSL_WANT_WRITE || result == ESP_TLS_ERR_SSL_TIMEOUT;
}
char Lower(char c) { return c >= 'A' && c <= 'Z' ? static_cast<char>(c + ('a' - 'A')) : c; }
bool Equal(const char* text, size_t size, const char* expected, bool ignoreCase = false) {
  if (size != std::strlen(expected)) { return false; }
  for (size_t i = 0; i < size; ++i) {
    if ((ignoreCase ? Lower(text[i]) : text[i]) != (ignoreCase ? Lower(expected[i]) : expected[i])) { return false; }
  }
  return true;
}
bool ConnectionUpgrade(const char* text, size_t size) {
  unsigned upgrades = 0;
  size_t at = 0;
  while (at < size) {
    while (at < size && (text[at] == ' ' || text[at] == '\t')) { ++at; }
    const size_t begin = at;
    while (at < size && text[at] != ',') { ++at; }
    size_t end = at;
    while (end > begin && (text[end - 1] == ' ' || text[end - 1] == '\t')) { --end; }
    if (begin == end) { return false; }
    for (size_t i = begin; i < end; ++i) {
      if (!((text[i] >= 'a' && text[i] <= 'z') || (text[i] >= 'A' && text[i] <= 'Z') ||
            (text[i] >= '0' && text[i] <= '9') || text[i] == '-')) { return false; }
    }
    if (Equal(text + begin, end - begin, "Upgrade", true)) { ++upgrades; }
    if (at < size && ++at == size) { return false; }
  }
  return upgrades == 1;
}
bool Utf8(uint8_t byte, unsigned& remaining, uint32_t& value, uint32_t& minimum) {
  if (!remaining) {
    if (byte <= 0x7f) { return true; }
    if (byte >= 0xc2 && byte <= 0xdf) { remaining = 1; value = byte & 0x1f; minimum = 0x80; }
    else if (byte >= 0xe0 && byte <= 0xef) { remaining = 2; value = byte & 0x0f; minimum = 0x800; }
    else if (byte >= 0xf0 && byte <= 0xf4) { remaining = 3; value = byte & 7; minimum = 0x10000; }
    else { return false; }
    return true;
  }
  if ((byte & 0xc0) != 0x80) { return false; }
  value = (value << 6) | (byte & 0x3f);
  if (--remaining) { return true; }
  return value >= minimum && value <= 0x10ffff && !(value >= 0xd800 && value <= 0xdfff);
}
bool ClosePayload(const uint8_t* data, size_t size) {
  if (size == 1) { return false; }
  if (!size) { return true; }
  const unsigned code = (static_cast<unsigned>(data[0]) << 8) | data[1];
  if (!((code >= 1000 && code <= 1014 && code != 1004 && code != 1005 && code != 1006) ||
        (code >= 3000 && code <= 4999))) { return false; }
  unsigned remaining = 0; uint32_t value = 0, minimum = 0;
  for (size_t i = 2; i < size; ++i) { if (!Utf8(data[i], remaining, value, minimum)) { return false; } }
  return !remaining;
}
}

EspIdfDeviceMediaSocket::~EspIdfDeviceMediaSocket() { Close(); }
void EspIdfDeviceMediaSocket::ResetParser() {
  scratchUsed_ = scratchAt_ = headerUsed_ = frameLength_ = frameAt_ = messageSize_ = 0;
  headerNeed_ = 2; frameReady_ = messageActive_ = false;
  workspace_ = nullptr; workspaceCapacity_ = 0;
  utf8Remaining_ = 0; utf8Value_ = utf8Minimum_ = 0;
}
void EspIdfDeviceMediaSocket::Close() {
  open_ = false;
  if (ssl_) { esp_transport_close(ssl_); esp_transport_destroy(ssl_); ssl_ = nullptr; }
  Wipe(send_, sizeof(send_)); Wipe(scratch_, sizeof(scratch_)); Wipe(control_, sizeof(control_));
  Wipe(header_, sizeof(header_)); ResetParser();
}
bool EspIdfDeviceMediaSocket::Fail(const char* code) { error_ = code; Close(); return false; }

bool EspIdfDeviceMediaSocket::Open(const std::string& host, const std::string& conversationId,
                                 const std::string& token, const std::string& key) {
  Close(); error_ = "invalid_open";
  if (!Host(host) || !Uuid(conversationId) || !Uuid(key) || !Token(token)) { return false; }
#if defined(CONFIG_MBEDTLS_DEBUG) && CONFIG_MBEDTLS_DEBUG
  // ESP-TLS debug callbacks can dump plaintext before encryption. A global log
  // tag suppression is racy with other clients enabling that same callback.
  return Fail("tls_debug_unsupported");
#else
  const int64_t deadline = esp_timer_get_time() + 5000000;
  ssl_ = esp_transport_ssl_init();
  if (!ssl_) { return Fail("tls_init"); }
  // A fresh SSL transport defaults to hostname verification enabled. Do not set
  // skip_common_name_check or custom common_name; SNI/verification use host.
  esp_transport_ssl_crt_bundle_attach(ssl_, esp_crt_bundle_attach);
  const int connectTimeout = Remaining(deadline, 5000);
  if (!connectTimeout || esp_transport_connect(ssl_, host.c_str(), 443, connectTimeout) != 0 ||
      !Remaining(deadline, 5000)) { return Fail("tls_connect"); }
  // The synchronous connector restores a blocking fd. Its five-second socket
  // timeout must not leak into partial TLS record reads after poll readiness.
  const int fd = esp_transport_get_socket(ssl_);
  const int flags = fd >= 0 ? fcntl(fd, F_GETFL, 0) : -1;
  if (flags < 0 || fcntl(fd, F_SETFL, flags | O_NONBLOCK) < 0) { return Fail("tls_nonblocking"); }
  if (!Upgrade(host, conversationId, token, key, deadline)) { return false; }
  open_ = true; error_ = ""; return true;
#endif
}

bool EspIdfDeviceMediaSocket::Upgrade(const std::string& host, const std::string& conversationId,
                                    const std::string& token, const std::string& key, int64_t deadline) {
  uint8_t random[16], digest[20]; unsigned char nonce[25]{}, accept[29]{};
  esp_fill_random(random, sizeof(random)); size_t encoded = 0;
  if (esp_crypto_base64_encode(nonce, sizeof(nonce), &encoded, random, sizeof(random)) != 0 || encoded != 24) {
    Wipe(random, sizeof(random)); return Fail("upgrade_crypto");
  }
  Wipe(random, sizeof(random));
  char challenge[61]{};
  std::snprintf(challenge, sizeof(challenge), "%s258EAFA5-E914-47DA-95CA-C5AB0DC85B11", nonce);
  if (esp_crypto_sha1(reinterpret_cast<const unsigned char*>(challenge), 60, digest) != 0 ||
      esp_crypto_base64_encode(accept, sizeof(accept), &encoded, digest, sizeof(digest)) != 0 || encoded != 28) {
    Wipe(digest, sizeof(digest)); return Fail("upgrade_crypto");
  }
  Wipe(digest, sizeof(digest));
  const int count = std::snprintf(reinterpret_cast<char*>(send_), sizeof(send_),
      "GET /v1/conversations/%s/device-media HTTP/1.1\r\nHost: %s\r\n"
      "Upgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\n"
      "Sec-WebSocket-Key: %s\r\nSec-WebSocket-Protocol: %s\r\n"
      "Authorization: Bearer %s\r\nIdempotency-Key: %s\r\n\r\n",
      conversationId.c_str(), host.c_str(), nonce, Protocol, token.c_str(), key.c_str());
  if (count <= 0 || static_cast<size_t>(count) >= sizeof(send_)) { return Fail("upgrade_size"); }
  const int timeout = Remaining(deadline, 250);
  const int written = timeout ? esp_transport_write(ssl_, reinterpret_cast<const char*>(send_), count, timeout) : -1;
  Wipe(send_, sizeof(send_));
  if (written != count || !Remaining(deadline, 250)) { return Fail("upgrade_write"); }
  // One byte per header read avoids consuming the first frame into a hidden
  // handshake buffer. The absolute deadline bounds trickled headers as well.
  size_t used = 0;
  while (used < 4096) {
    const int readTimeout = Remaining(deadline, 50);
    if (!readTimeout) { return Fail("upgrade_timeout"); }
    const int got = esp_transport_read(ssl_, reinterpret_cast<char*>(send_ + used), 1, readTimeout);
    if (ReadIdle(got)) { continue; }
    if (got < 0 || got > 1) { return Fail("upgrade_read"); }
    const uint8_t byte = send_[used];
    if ((byte < 0x20 && byte != '\r' && byte != '\n' && byte != '\t') || byte >= 0x7f) { return Fail("upgrade_header"); }
    if ((byte == '\n' && (!used || send_[used - 1] != '\r')) ||
        (used && send_[used - 1] == '\r' && byte != '\n')) { return Fail("upgrade_header"); }
    ++used;
    if (used >= 4 && std::memcmp(send_ + used - 4, "\r\n\r\n", 4) == 0) { break; }
  }
  if (used < 4 || used >= 4096 || !Remaining(deadline, 50)) { return Fail("upgrade_header"); }
  send_[used] = 0;
  char* line = reinterpret_cast<char*>(send_);
  char* end = std::strstr(line, "\r\n");
  if (!end || end - line < 12 || std::memcmp(line, "HTTP/1.1 101", 12) != 0 ||
      (end - line > 12 && line[12] != ' ')) { return Fail("upgrade_status"); }
  unsigned accepted = 0, upgraded = 0, connected = 0, protocol = 0;
  line = end + 2;
  while (*line) {
    end = std::strstr(line, "\r\n");
    if (!end) { return Fail("upgrade_header"); }
    if (end == line) { break; }
    char* colon = static_cast<char*>(std::memchr(line, ':', static_cast<size_t>(end - line)));
    if (!colon || colon == line || *line == ' ' || *line == '\t') { return Fail("upgrade_header"); }
    for (char* at = line; at < colon; ++at) {
      if (!((*at >= 'a' && *at <= 'z') || (*at >= 'A' && *at <= 'Z') ||
            (*at >= '0' && *at <= '9') || *at == '-')) { return Fail("upgrade_header"); }
    }
    const char* value = colon + 1;
    while (value < end && (*value == ' ' || *value == '\t')) { ++value; }
    char* trimmed = end;
    while (trimmed > value && (trimmed[-1] == ' ' || trimmed[-1] == '\t')) { --trimmed; }
    const size_t nameSize = static_cast<size_t>(colon - line), valueSize = static_cast<size_t>(trimmed - value);
    if (Equal(line, nameSize, "Sec-WebSocket-Accept", true)) {
      if (++accepted != 1 || !Equal(value, valueSize, reinterpret_cast<char*>(accept))) { return Fail("upgrade_accept"); }
    } else if (Equal(line, nameSize, "Upgrade", true)) {
      if (++upgraded != 1 || !Equal(value, valueSize, "websocket", true)) { return Fail("upgrade_upgrade"); }
    } else if (Equal(line, nameSize, "Connection", true)) {
      if (++connected != 1 || !ConnectionUpgrade(value, valueSize)) { return Fail("upgrade_connection"); }
    } else if (Equal(line, nameSize, "Sec-WebSocket-Protocol", true)) {
      if (++protocol != 1 || !Equal(value, valueSize, Protocol)) { return Fail("upgrade_protocol"); }
    } else if (Equal(line, nameSize, "Sec-WebSocket-Extensions", true)) { return Fail("upgrade_extensions"); }
    line = end + 2;
  }
  if (accepted != 1 || upgraded != 1 || connected != 1 || protocol != 1) { return Fail("upgrade_missing"); }
  Wipe(send_, sizeof(send_)); return true;
}

bool EspIdfDeviceMediaSocket::SendFrame(uint8_t opcode, const uint8_t* data, size_t size) {
  if (!ssl_ || size > sizeof(send_) - 8 || (size && !data)) { return Fail("send_size"); }
  const size_t maskAt = size < 126 ? 2 : 4, header = maskAt + 4;
  send_[0] = static_cast<uint8_t>(0x80 | opcode);
  send_[1] = static_cast<uint8_t>(0x80 | (size < 126 ? size : 126));
  if (size >= 126) { send_[2] = static_cast<uint8_t>(size >> 8); send_[3] = static_cast<uint8_t>(size); }
  esp_fill_random(send_ + maskAt, 4);
  for (size_t i = 0; i < size; ++i) { send_[header + i] = data[i] ^ send_[maskAt + i % 4]; }
  const int total = static_cast<int>(header + size);
  const int sent = esp_transport_write(ssl_, reinterpret_cast<const char*>(send_), total, 250);
  Wipe(send_, sizeof(send_));
  return sent == total || Fail("send_unknown");
}
bool EspIdfDeviceMediaSocket::Send(bool binary, const uint8_t* data, size_t size) {
  if (!open_) { return false; }
  if (size > sizeof(send_) - 8 || (size && !data)) { return Fail("send_size"); }
  if (!binary) {
    unsigned remaining = 0; uint32_t value = 0, minimum = 0;
    for (size_t i = 0; i < size; ++i) {
      if (!Utf8(data[i], remaining, value, minimum)) { return Fail("send_utf8"); }
    }
    if (remaining) { return Fail("send_utf8"); }
  }
  return SendFrame(binary ? 2 : 1, data, size);
}

bool EspIdfDeviceMediaSocket::ReadHeader(uint8_t* workspace, size_t capacity) {
  const uint8_t first = header_[0], second = header_[1];
  opcode_ = first & 15; fin_ = (first & 0x80) != 0;
  if ((first & 0x70) || (second & 0x80) ||
      !(opcode_ <= 2 || opcode_ == 8 || opcode_ == 9 || opcode_ == 10)) { return Fail("frame_header"); }
  uint64_t length = second & 127;
  if (length >= 126) {
    length = 0;
    for (size_t i = 2; i < headerNeed_; ++i) { length = (length << 8) | header_[i]; }
    if (((second & 127) == 126 && length < 126) ||
        ((second & 127) == 127 && (length < 65536 || (header_[2] & 0x80)))) { return Fail("frame_length"); }
  }
  if (length > MaxMessage || (opcode_ >= 8 && (!fin_ || length > 125))) { return Fail("frame_size"); }
  if (opcode_ < 8) {
    if (opcode_) {
      if (messageActive_) { return Fail("frame_interleave"); }
      messageActive_ = true; messageBinary_ = opcode_ == 2; messageSize_ = 0;
      workspace_ = workspace; workspaceCapacity_ = capacity;
    } else if (!messageActive_) { return Fail("frame_continuation"); }
    const size_t maximum = messageBinary_ ? MaxMessage : MaxMessage - 1;
    if (length > maximum - messageSize_ || length > capacity - messageSize_) { return Fail("message_size"); }
  }
  frameLength_ = static_cast<size_t>(length); frameAt_ = 0; frameReady_ = true; return true;
}
bool EspIdfDeviceMediaSocket::FinishControl() {
  if (opcode_ == 9) { return SendFrame(10, control_, frameLength_); }
  if (opcode_ == 8) {
    if (!ClosePayload(control_, frameLength_)) { return Fail("close_payload"); }
    if (!SendFrame(8, control_, frameLength_)) { return false; }
    error_ = "closed"; Close();
  }
  Wipe(control_, sizeof(control_)); return true;
}
DeviceMediaSocketPort::ReceiveResult EspIdfDeviceMediaSocket::Receive(
    uint8_t* workspace, size_t capacity, size_t& used, bool& binary) {
  used = 0; binary = false;
  if (!open_) { return error_[0] && std::strcmp(error_, "closed") != 0 ? ReceiveResult::Failed : ReceiveResult::Closed; }
  if (!workspace || !capacity || capacity > MaxMessage ||
      (messageActive_ && (workspace_ != workspace || workspaceCapacity_ != capacity))) {
    Fail("receive_workspace"); return ReceiveResult::Failed;
  }
  if (scratchAt_ == scratchUsed_) {
    Wipe(scratch_, sizeof(scratch_)); scratchAt_ = scratchUsed_ = 0;
    const int got = esp_transport_read(ssl_, reinterpret_cast<char*>(scratch_), sizeof(scratch_), 50);
    if (ReadIdle(got)) { return ReceiveResult::Idle; }
    if (got < 0 || static_cast<size_t>(got) > sizeof(scratch_)) { Fail("receive_io"); return ReceiveResult::Failed; }
    scratchUsed_ = static_cast<size_t>(got);
  }
  // Consume at most this one bounded TLS chunk. Never wait for a whole frame or
  // message; a trickling peer yields the actor after each underlying read.
  while (scratchAt_ < scratchUsed_ || frameReady_) {
    if (!frameReady_) {
      header_[headerUsed_++] = scratch_[scratchAt_++];
      if (headerUsed_ == 2) {
        if ((header_[0] & 0x70) || (header_[1] & 0x80)) { Fail("frame_header"); return ReceiveResult::Failed; }
        headerNeed_ = (header_[1] & 127) == 126 ? 4 : (header_[1] & 127) == 127 ? 10 : 2;
      }
      if (headerUsed_ < headerNeed_) { continue; }
      if (!ReadHeader(workspace, capacity)) { return ReceiveResult::Failed; }
    }
    const size_t take = std::min(frameLength_ - frameAt_, scratchUsed_ - scratchAt_);
    if (opcode_ >= 8) { std::memcpy(control_ + frameAt_, scratch_ + scratchAt_, take); }
    else {
      for (size_t i = 0; i < take; ++i) {
        const uint8_t byte = scratch_[scratchAt_ + i];
        if (!messageBinary_ && !Utf8(byte, utf8Remaining_, utf8Value_, utf8Minimum_)) {
          Fail("text_utf8"); return ReceiveResult::Failed;
        }
        workspace[messageSize_++] = byte;
      }
    }
    scratchAt_ += take; frameAt_ += take;
    if (frameAt_ != frameLength_) { break; }
    frameReady_ = false; headerUsed_ = 0; headerNeed_ = 2;
    if (opcode_ >= 8) {
      if (!FinishControl()) { return ReceiveResult::Failed; }
      if (!open_) { return ReceiveResult::Closed; }
      // Bound control work to one frame per actor visit, including at most one
      // PONG write. A ping flood must not monopolize command admission.
      return ReceiveResult::Idle;
    } else if (fin_) {
      if (utf8Remaining_) { Fail("text_utf8"); return ReceiveResult::Failed; }
      used = messageSize_; binary = messageBinary_;
      messageActive_ = false; messageSize_ = 0; workspace_ = nullptr;
      return ReceiveResult::Message;
    }
  }
  return ReceiveResult::Idle;
}

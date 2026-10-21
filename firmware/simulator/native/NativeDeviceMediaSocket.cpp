#include "NativeDeviceMediaSocket.h"
#include <algorithm>
bool NativeDeviceMediaSocket::Open(const std::string&, const std::string& conversation,
    const std::string&, const std::string& key) {
  if (attempted_) return opened_;
  attempted_ = true; path_ = "/v1/conversations/" + conversation + "/device-media";
  auto reply = exchange_("open", path_, key, "application/json", nullptr, 0);
  fake_ = reply.status == 299;
  opened_ = reply.status == 204;
  if (!opened_ && !fake_) error_ = "media_connect_failed";
  std::fill(reply.bytes.begin(), reply.bytes.end(), 0);
  return opened_;
}
bool NativeDeviceMediaSocket::Send(bool binary, const uint8_t* data, size_t size) {
  if (!opened_ || !data || !size || size > 8184) return false;
  auto reply = exchange_("send", path_, "", binary ? "application/octet-stream" : "application/json", data, size);
  std::fill(reply.bytes.begin(), reply.bytes.end(), 0);
  if (reply.status == 204) return true;
  error_ = "media_send_unknown"; Close(); return false;
}
DeviceMediaSocketPort::ReceiveResult NativeDeviceMediaSocket::Receive(uint8_t* workspace,
    size_t capacity, size_t& used, bool& binary) {
  used = 0;
  if (!opened_) return ReceiveResult::Closed;
  auto reply = exchange_("receive", path_, "", "application/json", nullptr, 0);
  if (reply.status == 204) return ReceiveResult::Idle;
  const bool valid = reply.status == 200 && !reply.bytes.empty() && reply.bytes.size() <= capacity
      && (reply.type == "application/octet-stream" || reply.type == "application/json");
  if (valid) {
    used = reply.bytes.size(); binary = reply.type == "application/octet-stream";
    std::copy(reply.bytes.begin(), reply.bytes.end(), workspace);
  }
  std::fill(reply.bytes.begin(), reply.bytes.end(), 0);
  if (valid) return ReceiveResult::Message;
  error_ = "media_receive_failed"; Close(); return ReceiveResult::Failed;
}
void NativeDeviceMediaSocket::Close() {
  if (!opened_) return;
  opened_ = false;
  auto reply = exchange_("close", path_, "", "application/json", nullptr, 0);
  std::fill(reply.bytes.begin(), reply.bytes.end(), 0);
}

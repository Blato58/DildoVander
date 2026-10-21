#pragma once
#include "voice_turn/DeviceMediaSocketPort.h"
#include <functional>
#include <vector>

// Private simulator I/O only. Backend credentials never cross this port.
class NativeDeviceMediaSocket final : public DeviceMediaSocketPort {
 public:
  struct Reply { int status; std::string type; std::vector<uint8_t> bytes; };
  using Exchange = std::function<Reply(const std::string&, const std::string&,
      const std::string&, const std::string&, const uint8_t*, size_t)>;
  explicit NativeDeviceMediaSocket(Exchange exchange) : exchange_(std::move(exchange)) {}
  ~NativeDeviceMediaSocket() override { Close(); }
  bool Open(const std::string&, const std::string& conversation, const std::string&, const std::string& key) override;
  bool Send(bool binary, const uint8_t* data, size_t size) override;
  ReceiveResult Receive(uint8_t* workspace, size_t capacity, size_t& used, bool& binary) override;
  void Close() override;
  const char* Error() const override { return error_; }
  bool Fake() const { return fake_; }
 private:
  Exchange exchange_;
  std::string path_;
  bool opened_ = false, attempted_ = false, fake_ = false;
  const char* error_ = "";
};

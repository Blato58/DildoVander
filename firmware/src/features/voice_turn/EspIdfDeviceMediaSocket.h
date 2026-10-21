#pragma once
#include "DeviceMediaSocketPort.h"
#include "esp_transport.h"

// Bounded RFC6455 over ESP-IDF's verified SSL transport. No transport_ws: its
// exact-read loops and raw upgrade logging do not meet this actor's contract.
class EspIdfDeviceMediaSocket final : public DeviceMediaSocketPort {
 public:
  EspIdfDeviceMediaSocket() = default;
  EspIdfDeviceMediaSocket(const EspIdfDeviceMediaSocket&) = delete;
  EspIdfDeviceMediaSocket& operator=(const EspIdfDeviceMediaSocket&) = delete;
  ~EspIdfDeviceMediaSocket() override;
  bool Open(const std::string& host, const std::string& conversationId,
            const std::string& token, const std::string& idempotencyKey) override;
  bool Send(bool binary, const uint8_t* data, size_t size) override;
  ReceiveResult Receive(uint8_t* workspace, size_t capacity,
                        size_t& used, bool& binary) override;
  void Close() override;
  const char* Error() const override { return error_; }

 private:
  bool Fail(const char* code);
  bool SendFrame(uint8_t opcode, const uint8_t* data, size_t size);
  bool Upgrade(const std::string& host, const std::string& conversationId,
               const std::string& token, const std::string& key, int64_t deadline);
  bool ReadHeader(uint8_t* workspace, size_t capacity);
  bool FinishControl();
  void ResetParser();
  esp_transport_handle_t ssl_ = nullptr;
  const char* error_ = "closed";
  bool open_ = false;
  uint8_t scratch_[2048]{};
  // Reused for the bounded HTTP upgrade then masked outgoing frames: payload
  // max 8184 plus frame overhead fits 8192. Always scrubbed after write, including
  // short/unknown writes. Framework TLS copies are outside this object's zeroing
  // guarantee.
  uint8_t send_[8192]{};
  uint8_t control_[125]{};
  uint8_t header_[10]{};
  size_t scratchUsed_ = 0, scratchAt_ = 0;
  size_t headerUsed_ = 0, headerNeed_ = 2;
  size_t frameLength_ = 0, frameAt_ = 0, messageSize_ = 0;
  uint8_t opcode_ = 0;
  bool frameReady_ = false, fin_ = false, messageActive_ = false, messageBinary_ = false;
  uint8_t* workspace_ = nullptr;
  size_t workspaceCapacity_ = 0;
  unsigned utf8Remaining_ = 0;
  uint32_t utf8Value_ = 0, utf8Minimum_ = 0;
};

#pragma once
#include <array>
#include <atomic>
#include <functional>
#include <string>
#include <vector>
#include "DeviceMediaSocketPort.h"
#include "DeviceMediaCodec.h"
#include "RealtimeSessionEngine.h"

// Exactly one socket actor calls Step, Submit, Poll and Abandon. The playback
// worker is independent. Stop only revokes RAM authority; it never waits for TLS.
class DeviceMediaSession final : public VoiceControlPort {
 public:
  using Clock = std::function<uint64_t()>;
  using Capture = std::function<bool(std::vector<uint8_t>&)>;
  DeviceMediaSession(DeviceMediaSocketPort& socket, Clock now,
      std::function<std::string()> requestId, Capture capture,
      uint8_t* receiveWorkspace, size_t receiveCapacity);
  ~DeviceMediaSession();
  void Attach(RealtimeSessionEngine& engine) { engine_ = &engine; }
  bool Open(const std::string& host, const TurnCorrelation& opening,
      const std::string& token, const std::string& key, bool live, const std::string& request);
  bool Step();
  bool Submit(const Command& command, Handle& handle) override;
  Progress Poll(Handle handle) override;
  void Abandon(Handle handle) override;
  void Stop() override { stopped_.store(true); }
  const char* Error() const { return error_; }
 private:
  struct Reply {
    Handle handle = 0;
    std::string id, wire;
    uint64_t sentAt = 0;
    bool sent = false, abandoned = false, complete = false, success = false;
  };
  bool Fail(const char* code);
  bool Message(bool binary, const uint8_t* data, size_t length);
  bool Control(const kufr_media::Control& control);
  bool PumpControl();
  bool PumpInput();
  void ClearInput();
  DeviceMediaSocketPort& socket_;
  Clock now_;
  std::function<std::string()> requestId_;
  Capture capture_;
  RealtimeSessionEngine* engine_ = nullptr;
  uint8_t* receive_;
  size_t receiveCapacity_;
  std::array<Reply, 4> replies_{};
  std::array<std::string, 128> requestIds_{};
  size_t requestCount_ = 0;
  Handle nextHandle_ = 0, waitingReply_ = 0;
  std::atomic<bool> stopped_{false};
  bool opened_ = false, inputWaiting_ = false;
  uint64_t openedAt_ = 0, inputSentAt_ = 0, outputGeneration_ = 0;
  uint32_t highestStream_ = 0, inputStream_ = 0, outputStream_ = 0;
  uint32_t inputSequence_ = 0, outputSequence_ = 0, waitingInputStream_ = 0, waitingInputSequence_ = 0;
  uint32_t capturedEpoch_ = 0;
  size_t inputBytes_ = 0, receivedBytes_ = 0, capturedOffset_ = 0;
  std::string inputId_, operationId_;
  uint64_t operationGeneration_ = 0;
  std::vector<uint8_t> captured_;
  std::array<uint8_t, 4112> inputFrame_{};
  const char* error_ = "";
};

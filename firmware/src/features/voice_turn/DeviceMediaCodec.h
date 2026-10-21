#pragma once
#include <cstddef>
#include <cstdint>
#include <string>

namespace kufr_media {
constexpr size_t HeaderBytes = 16, MaximumPcmBytes = 4096;
enum class ControlKind { Input, Output, Operation, InputAck, ControlAck };
struct Control {
  ControlKind kind = ControlKind::Input;
  uint32_t stream = 0, sequence = 0;
  uint64_t generation = 0;
  unsigned status = 0;
  std::string inputId, operationId, requestId, code;
};
struct PcmFrame {
  uint32_t stream = 0, sequence = 0;
  const uint8_t* pcm = nullptr;
  size_t bytes = 0;
};
// Borrowed PCM remains valid only while its complete receive buffer is alive.
bool ReadOutputPcm(const uint8_t* message, size_t length, PcmFrame& frame);
bool WriteInputPcm(uint32_t stream, uint32_t sequence, const uint8_t* pcm,
                   size_t length, uint8_t* message, size_t capacity, size_t& used);
// Only additive DeviceMedia controls; retained engine events use their existing
// strict RealtimeEventCodec. Unknown/duplicate fields are never ignored.
bool ReadControl(const char* message, size_t length, Control& control);
}

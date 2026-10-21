#include "../src/features/voice_turn/DeviceMediaCodec.h"
#include <array>
#include <cassert>
#include <cstring>
#include <iostream>
#include <initializer_list>
#include <string>

using namespace kufr_media;
int main() {
  const std::string uuid = "12345678-1234-1234-1234-123456789abc";
  const std::string input = "{\"type\":\"media.input\",\"stream\":7,\"inputId\":\"" + uuid +
      "\",\"format\":\"pcm16-mono-24000\",\"maxFrameBytes\":4096,\"credit\":1}";
  Control control;
  auto read = [&](const std::string& value) { return ReadControl(value.data(), value.size(), control); };
  assert(read(input) && control.kind == ControlKind::Input && control.inputId == uuid && control.stream == 7);
  assert(read("{\"type\":\"media.output\",\"stream\":4294967295,\"responseGeneration\":0,\"format\":\"pcm16-mono-24000\"}"));
  assert(read("{\"type\":\"media.operation\",\"operationId\":\"" + uuid + "\",\"responseGeneration\":9007199254740991}"));
  assert(read("{\"type\":\"media.ack\",\"stream\":1,\"sequence\":0}"));
  assert(read("{\"type\":\"control.ack\",\"requestId\":\"" + uuid + "\",\"status\":204}"));
  assert(read("{\"type\":\"control.ack\",\"requestId\":\"" + uuid + "\",\"status\":409,\"code\":\"conversation_restart_required\"}"));
  for (const auto& invalid : std::initializer_list<std::string>{"{}", input + "x", input.substr(0, input.size() - 1) + ",\"stream\":7}",
      "{\"type\":\"media.ack\",\"stream\":0,\"sequence\":0}",
      "{\"type\":\"media.ack\",\"stream\":1,\"sequence\":4294967295}",
      "{\"type\":\"media.ack\",\"stream\":1,\"sequence\":01}",
      "{\"type\":\"control.ack\",\"requestId\":\"" + uuid + "\",\"status\":409}",
      "{\"type\":\"control.ack\",\"requestId\":\"" + uuid + "\",\"status\":204,\"code\":\"media_cancel_mismatch\"}"}) assert(!read(invalid));
  std::array<uint8_t, HeaderBytes + MaximumPcmBytes> bytes{};
  const uint8_t pcm[] = {1, 2, 3, 4};
  size_t used = 0;
  assert(WriteInputPcm(7, 3, pcm, sizeof(pcm), bytes.data(), bytes.size(), used) && used == 20);
  const uint8_t expected[] = {'K','F',1,1,7,0,0,0,3,0,0,0,4,0,0,0,1,2,3,4};
  assert(std::memcmp(expected, bytes.data(), sizeof(expected)) == 0);
  PcmFrame frame;
  assert(!ReadOutputPcm(bytes.data(), used, frame)); // Direction cannot be confused.
  bytes[3] = 2;
  assert(ReadOutputPcm(bytes.data(), used, frame) && frame.stream == 7 && frame.sequence == 3 && frame.bytes == 4);
  assert(std::memcmp(frame.pcm, pcm, sizeof(pcm)) == 0);
  bytes[12] = 2; assert(!ReadOutputPcm(bytes.data(), used, frame));
  bytes[12] = 4; assert(!ReadOutputPcm(bytes.data(), used - 1, frame));
  assert(!WriteInputPcm(7, UINT32_MAX, pcm, sizeof(pcm), bytes.data(), bytes.size(), used) && used == 0);
  std::cout << "Device media strict controls, canonical identifiers and exact KF binary framing passed.\n";
}

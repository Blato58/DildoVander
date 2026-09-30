#pragma once

#include <stddef.h>
#include <stdint.h>

#include <string>

#include "../device_control/DeviceControl.h"

enum class LocalControlMessageKind : uint8_t {
  Hello,
  StateGet,
  Acquire,
  Release,
  Heartbeat,
  Text,
  Cancel,
  Stop,
  Reset,
  Selection,
  Reload,
};

struct LocalControlMessage {
  LocalControlMessageKind kind = LocalControlMessageKind::Hello;
  std::string messageId;
  std::string deviceId;
  std::string bootId;
  std::string controlLeaseId;
  std::string conversationId;
  std::string clientTurnId;
  std::string text;
  std::string commandId;
  uint64_t generation = 0;
  uint64_t expectedRevision = 0;
  uint64_t sequence = 0;
  uint64_t issuedAtDeviceMs = 0;
  uint32_t maxAgeMs = 0;
  uint8_t compartment = 0;
  SelectionSource source = SelectionSource::Manual;
};

// Only the v1 client-to-device subset; output events are never input commands.
// Rejects duplicates, extra fields, non-integer number syntax and invalid UTF-8.
bool ParseLocalControlMessage(const char* data, size_t length,
                              LocalControlMessage& message);

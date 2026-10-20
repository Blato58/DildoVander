#pragma once

#include <array>
#include <stdint.h>
#include <string>
#include <algorithm>

template <size_t Capacity> struct PresentationIdentity {
  std::array<char, Capacity> bytes{};
  PresentationIdentity& operator=(const std::string& value) {
    bytes.fill(0);
    std::copy_n(value.data(), std::min(value.size(), Capacity - 1), bytes.data());
    return *this;
  }
  const char* data() const { return bytes.data(); }
};

// Owned by the existing control task. Copy only while serialized with that
// owner; exporting the copy is observer work and never runs in Tick.
struct PresentationTraceEvent {
  uint32_t sequence = 0;
  const char* kind = "accepted";
  const char* phaseId = "prepare";
  const char* evidence = "commanded";
  const char* reason = "";
  const char* clockDomain = "device-monotonic";
  uint64_t timeMs = 0, dueAt = 0;
  uint32_t latenessMs = 0;
  uint8_t phaseIndex = 0, slot = 0, mask = 0;
  uint8_t red = 0, green = 0, blue = 0, level = 0;
  bool vibration = false;
  uint16_t toneHz = 0;
};

struct PresentationDiagnostics {
  static constexpr size_t Capacity = 64;
  PresentationIdentity<97> operationId;
  PresentationIdentity<65> bootId;
  PresentationIdentity<97> track;
  uint32_t audioGeneration = 0, audioPositionMs = 0;
  uint64_t beatIndex = 0;
  const char* sceneId = "";
  const char* sceneHash = "";
  const char* variant = "";
  uint32_t sceneVersion = 0;
  uint64_t generation = 0, deviceRevision = 0, calibrationId = 0;
  uint8_t slot = 0;
  const char* state = "idle";
  const char* evidence = "unknown";
  bool reduced = false;
  bool none = true;
  uint32_t lastSequence = 0, droppedEvents = 0;
  size_t count = 0, first = 0;
  std::array<PresentationTraceEvent, Capacity> events{};

  void Append(PresentationTraceEvent event) {
    event.sequence = ++lastSequence;
    if (count == Capacity) {
      first = (first + 1) % Capacity;
      ++droppedEvents;
    } else {
      ++count;
    }
    events[(first + count - 1) % Capacity] = event;
  }
};

std::string ExportPresentationTrace(const PresentationDiagnostics& diagnostics);

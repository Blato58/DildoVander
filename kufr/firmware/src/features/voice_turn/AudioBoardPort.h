#pragma once

// Slow expander writes must run under a task mutex, never a spinlock or ISR.
// The hardware adapter verifies the B wiring and the SD level interface first.
class AudioBoardPort {
 public:
  virtual ~AudioBoardPort() = default;
  virtual bool AudioReady() const = 0;
  virtual bool EnableAmplifiers(bool enabled) = 0;
};

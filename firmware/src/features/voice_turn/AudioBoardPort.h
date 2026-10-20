#pragma once

// A verified B wiring profile. Its amplifiers remain in their factory mono
// configuration; mute is software-only and never writes their SD pins.
class AudioBoardPort {
 public:
  virtual ~AudioBoardPort() = default;
  virtual bool AudioReady() const = 0;
};

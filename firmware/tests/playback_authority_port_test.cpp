#include "../src/features/voice_turn/PlaybackAuthorityPort.h"
#include <cassert>
#include <iostream>

namespace {
struct Raw final : BoundedPlaybackOwner::Port {
  bool* current;
  bool revokeDuringBorrow = false;
  unsigned enabled = 0;
  explicit Raw(bool& value) : current(&value) {}
  bool Check(const BoundedPlaybackOwner::Fence& fence) {
    if (revokeDuringBorrow) *current = false;
    if (!fence.Current()) return false;
    ++enabled; return true;
  }
  bool Begin(const BoundedPlaybackOwner::Fence& fence) override { return Check(fence); }
  bool Play(const BoundedPlaybackOwner::Fence& fence, const uint8_t*, size_t) override { return Check(fence); }
  bool Drain(const BoundedPlaybackOwner::Fence& fence) override { return Check(fence); }
  bool CancelPlayback(const BoundedPlaybackOwner::Fence& fence) override { return Check(fence); }
};
}
int main() {
  bool current = true, original = true;
  Raw raw(current); PlaybackAuthorityPort port(raw, [&] { return current; });
  BoundedPlaybackOwner::Fence fence{{1, 0}, &original,
      [](void* context, BoundedPlaybackOwner::Scope) { return *static_cast<bool*>(context); }};
  const uint8_t pcm[2] = {};
  assert(port.Begin(fence) && raw.enabled == 1);
  // Revocation after entering the borrowed raw call must still fence its own
  // final output-enable check; an owner-only token cannot see this Stop.
  raw.revokeDuringBorrow = true;
  assert(!port.Begin(fence) && !port.Play(fence, pcm, sizeof(pcm)) && !port.Drain(fence) && !port.CancelPlayback(fence));
  assert(raw.enabled == 1);
  raw.revokeDuringBorrow = false; current = true; original = false;
  assert(!port.Begin(fence) && !port.Play(fence, pcm, sizeof(pcm)));
  original = true;
  assert(port.Begin(fence));
  std::cout << "Playback raw authority composes session Stop and exact owner token at borrowed enable/write boundaries.\n";
}

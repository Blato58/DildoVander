#include "../src/features/voice_turn/BoundedVoicePlayback.h"
#include <array>
#include <cassert>
#include <iostream>
#include <vector>

namespace {
struct Raw final : BoundedPlaybackOwner::Port {
  using Fence = BoundedPlaybackOwner::Fence;
  bool active = false;
  unsigned stops = 0, mutes = 0;
  std::vector<char> calls;
  bool Begin(const Fence& fence) override {
    if (!fence.Current()) return false;
    calls.push_back('b'); active = true; return true;
  }
  bool Play(const Fence& fence, const uint8_t*, size_t length) override {
    assert(active && fence.Current() && length == 2);
    calls.push_back('p'); return true;
  }
  bool Drain(const Fence& fence) override {
    assert(active && fence.Current()); calls.push_back('d'); return true;
  }
  bool CancelPlayback(const Fence& fence) override {
    assert(active && fence.Current()); calls.push_back('c'); return true;
  }
  static void Stop(void* context) {
    auto& self = *static_cast<Raw*>(context);
    self.active = false; ++self.stops;
  }
  static bool Mute(void* context) { ++static_cast<Raw*>(context)->mutes; return true; }
};
using Progress = VoicePlaybackPort::Progress;
}
int main() {
  Raw raw;
  std::array<uint8_t, BoundedPlaybackOwner::WorkspaceBytes> workspace{};
  BoundedPlaybackOwner owner(raw, workspace.data(), workspace.size(), Raw::Stop, Raw::Mute, &raw);
  BoundedVoicePlayback first(owner, 1);
  assert(first.Begin() == Progress::Pending);
  assert(owner.RunOne());
  assert(first.Begin() == Progress::Succeeded);
  BoundedVoicePlayback duplicate(owner, 1), neverStarted(owner, 1);
  const auto initialStops = raw.stops;
  neverStarted.Stop();
  assert(duplicate.Begin() == Progress::Failed);
  duplicate.Stop();
  assert(raw.stops == initialStops && raw.active && !first.Failed());
  const uint8_t pcm[] = {1, 2};
  assert(first.Queue(0, pcm, sizeof(pcm)));
  assert(first.Drain(0) == Progress::Pending);
  assert(first.Queue(0, pcm, sizeof(pcm))); // Later PCM is behind the original frontier.
  assert(owner.RunOne() && raw.calls.back() == 'p');
  assert(owner.RunOne() && raw.calls.back() == 'd');
  assert(!owner.RunOne()); // Unconsumed frontier receipt still fences later Play.
  assert(first.Drain(0) == Progress::Succeeded);
  assert(owner.RunOne() && raw.calls.back() == 'p');
  assert(first.Drain(0) == Progress::Pending); // Later samples are not already drained.
  assert(owner.RunOne() && raw.calls.back() == 'd');
  assert(first.Drain(0) == Progress::Succeeded);
  assert(first.Drain(0) == Progress::Succeeded); // No duplicate raw drain.
  assert(first.Cancel(2) == Progress::Pending);
  assert(first.Queue(2, pcm, sizeof(pcm)));
  assert(first.Drain(2) == Progress::Pending); // Polling cancel never invents a drain.
  assert(owner.RunOne() && raw.calls.back() == 'c');
  assert(first.Cancel(2) == Progress::Succeeded);
  assert(owner.RunOne() && raw.calls.back() == 'p');
  assert(first.Drain(2) == Progress::Pending);
  assert(owner.RunOne() && raw.calls.back() == 'd');
  assert(first.Drain(2) == Progress::Succeeded);
  BoundedVoicePlayback next(owner, 2);
  assert(next.Begin() == Progress::Pending);
  assert(owner.RunOne());
  assert(next.Begin() == Progress::Succeeded);
  const auto stops = raw.stops;
  first.Stop();
  assert(raw.stops == stops && raw.active && !next.Failed());
  assert(!first.Queue(2, pcm, sizeof(pcm))); // A failed stale facade cannot stop its successor.
  assert(first.Failed() && raw.stops == stops && raw.active && !next.Failed());
  assert(next.Queue(0, pcm, sizeof(pcm)));
  next.Stop();
  assert(next.Failed() && !raw.active);
  for (const auto byte : workspace) assert(byte == 0);
  std::cout << "Scoped playback facade pending frontiers, queued continuation, cancellation and stale cleanup passed.\n";
}

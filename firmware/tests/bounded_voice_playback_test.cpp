#include "../src/features/voice_turn/BoundedVoicePlayback.h"
#include <array>
#include <algorithm>
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
void OrderedExplicitFrontiers() {
  Raw raw;
  std::array<uint8_t, BoundedPlaybackOwner::WorkspaceBytes> workspace{};
  BoundedPlaybackOwner owner(raw, workspace.data(), workspace.size(), Raw::Stop, Raw::Mute, &raw);
  BoundedVoicePlayback port(owner, 1);
  assert(port.Begin() == Progress::Pending && owner.RunOne() && port.Begin() == Progress::Succeeded);
  const uint8_t pcm[] = {1, 2};
  std::array<VoicePlaybackPort::DrainHandle, 4> handles{};
  for (auto& handle : handles) {
    assert(port.Queue(0, pcm, sizeof(pcm)));
    assert(port.RequestDrain(0, handle));
  }
  VoicePlaybackPort::DrainHandle overflow = 99;
  assert(!port.RequestDrain(0, overflow) && overflow == 0 && !port.Failed());
  assert(port.Queue(0, pcm, sizeof(pcm))); // Beyond all four original frontiers.
  assert(port.PollDrain(handles.back()) == Progress::Pending);
  for (size_t i = 0; i != handles.size(); ++i) {
    assert(owner.RunOne() && raw.calls.back() == 'p');
    assert(owner.Inspect().playedSamples == i + 1);
    assert(owner.RunOne() && raw.calls.back() == 'd');
    assert(!owner.RunOne()); // Receipt consumption cannot expose later PCM early.
    assert(port.PollDrain(handles[i]) == Progress::Succeeded);
    assert(port.PollDrain(handles[i]) == Progress::Failed && !port.Failed());
  }
  assert(owner.RunOne() && raw.calls.back() == 'p' && owner.Inspect().playedSamples == 5);
  const auto previousDrains = std::count(raw.calls.begin(), raw.calls.end(), 'd');
  for (auto& handle : handles) assert(port.RequestDrain(0, handle));
  assert(owner.RunOne() && raw.calls.back() == 'd' && !owner.RunOne());
  for (auto handle : handles) assert(port.PollDrain(handle) == Progress::Succeeded);
  assert(std::count(raw.calls.begin(), raw.calls.end(), 'd') == previousDrains + 1);
  assert(port.Cancel(3) == Progress::Pending);
  assert(port.Queue(3, pcm, sizeof(pcm)) && port.RequestDrain(3, handles[0]));
  assert(owner.RunOne() && raw.calls.back() == 'c');
  assert(owner.RunOne() && raw.calls.back() == 'p');
  assert(owner.RunOne() && raw.calls.back() == 'd');
  assert(port.Cancel(3) == Progress::Succeeded);
  assert(port.PollDrain(handles[0]) == Progress::Succeeded);
  assert(port.Queue(3, pcm, sizeof(pcm)) && port.RequestDrain(3, handles[0]));
  port.AbandonDrain(handles[0]);
  assert(port.Cancel(4) == Progress::Pending);
  assert(port.PollDrain(handles[0]) == Progress::Failed);
  assert(owner.RunOne() && raw.calls.back() == 'c' && port.Cancel(4) == Progress::Succeeded);
  assert(port.Queue(4, pcm, sizeof(pcm)) && port.RequestDrain(4, handles[1]));
  assert(owner.RunOne() && owner.RunOne() && port.PollDrain(handles[1]) == Progress::Succeeded);
  port.Stop();
}
void CachedFrontiersDoNotSurviveRevocation() {
  for (bool successor : {false, true}) {
    Raw raw;
    std::array<uint8_t, BoundedPlaybackOwner::WorkspaceBytes> workspace{};
    BoundedPlaybackOwner owner(raw, workspace.data(), workspace.size(), Raw::Stop, Raw::Mute, &raw);
    BoundedVoicePlayback first(owner, 1), second(owner, 2);
    assert(first.Begin() == Progress::Pending && owner.RunOne() && first.Begin() == Progress::Succeeded);
    const uint8_t pcm[] = {1, 2};
    VoicePlaybackPort::DrainHandle a = 0, b = 0, rejected = 0;
    assert(first.Queue(0, pcm, sizeof(pcm)) && first.RequestDrain(0, a) && first.RequestDrain(0, b));
    assert(owner.RunOne() && owner.RunOne() && first.PollDrain(a) == Progress::Succeeded);
    if (successor) {
      assert(second.Begin() == Progress::Pending && owner.RunOne() && second.Begin() == Progress::Succeeded);
    } else first.Stop();
    const auto stops = raw.stops;
    assert(first.PollDrain(b) == Progress::Failed);
    assert(!first.RequestDrain(0, rejected) && rejected == 0 && raw.stops == stops);
    if (successor) {
      assert(raw.active && !second.Failed());
      assert(second.RequestDrain(0, a) && second.PollDrain(a) == Progress::Succeeded);
      second.Stop();
    }
  }
}
}
int main() {
  OrderedExplicitFrontiers();
  CachedFrontiersDoNotSurviveRevocation();
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
